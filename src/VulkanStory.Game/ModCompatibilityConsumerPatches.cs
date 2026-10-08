using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Mono.Cecil;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using GameMod = Vintagestory.API.Common.Mod;

namespace VulkanStory.Game;

/// <summary>Refuses unadapted third-party direct GL before enabled mod code initializes under the active Vulkan renderer.</summary>
/// <remarks>Game RenderAPI calls retain ordinary routing. No third-party direct GL operation is currently declared supported.</remarks>
internal static class ModCompatibilityConsumerPatches
{
    private const string Owner = "vulkanstory.routing.mod-compatibility";
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ModGlInspection> Inspections = new(StringComparer.Ordinal);
    private static readonly HashSet<ModContainer> Refused = new();
    private static ProcessRuntime? runtime;
    private static System.Func<ModLoader, List<ModContainer>, List<ModContainer>>? verify;
    private static ReadModInfo? readInfo;
    private static System.Action<GameMod, ModInfo>? setInfo;

    /// <summary>Invokes the official Cecil-based mod-attribute reader without loading a source-mod assembly.</summary>
    /// <param name="mod">Original mod container whose attribute semantics the game owns.</param>
    /// <param name="assembly">Unloaded emitted assembly metadata.</param>
    /// <param name="configuration">Original world configuration read from assembly attributes.</param>
    /// <returns>Original mod metadata or null when its required attribute is absent.</returns>
    private delegate ModInfo? ReadModInfo(ModContainer mod, AssemblyDefinition assembly, out ModWorldConfiguration? configuration);

    /// <summary>Returns the discovered image decisions and explicitly states the static scan's proof boundary.</summary>
    internal static string Status
    {
        get
        {
            lock (Gate)
                return "Mod GL discovery (latest inspected images): " + (Inspections.Count == 0 ? "no third-party images inspected" :
                    string.Join("\n", Inspections.Values.Select(result => result.Detail))) +
                    "\nGame RenderAPI uses ordinary renderer routing; direct GL has no supported third-party adapter. " +
                    "Reflection, dynamic/custom loaders, external managed dependencies and native dependency internals are unverified.";
        }
    }

    /// <summary>Creates startup-bound discovery with exact official signatures and checked original IL anchors.</summary>
    /// <param name="owner">Early runtime whose committed renderer determines whether refusal applies.</param>
    /// <returns>One dormant group installed and removed with the complete graphics transaction.</returns>
    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner)
    {
        var harmony = new Harmony(Owner);
        MethodInfo? instantiate = null, compile = null, verifier = null, metadataReader = null, infoSetter = null;
        bool attempted = false;
        return new StartupPatchGroup("graphics-mod-compatibility", () =>
        {
            if (typeof(ModLoader).Module.ModuleVersionId != new Guid("8a3ad3aa-2940-4c39-ae1b-881de9e65346"))
                throw new InvalidOperationException("Mod discovery requires the official Vintage Story 1.22.7 Lib profile.");
            instantiate = Method(typeof(ModLoader), "instantiateMods", typeof(List<ModSystem>), typeof(List<ModContainer>));
            compile = Method(typeof(ModCompilationContext), "CompileFromFiles", typeof(Assembly), typeof(ModContainer));
            verifier = Method(typeof(ModLoader), "verifyMods", typeof(List<ModContainer>), typeof(List<ModContainer>));
            metadataReader = Method(typeof(ModContainer), "LoadModInfoFromAssemblyDefinition", typeof(ModInfo),
                typeof(AssemblyDefinition), typeof(ModWorldConfiguration).MakeByRefType());
            infoSetter = Method(typeof(GameMod), "set_Info", typeof(void), typeof(ModInfo));
            if (AccessTools.Field(typeof(ModLoader), "side")?.FieldType != typeof(EnumAppSide))
                throw new MissingFieldException("Official mod loader side changed.");
            CheckCompile(PatchProcessor.GetOriginalInstructions(compile));
            _ = RecheckBoundary(PatchProcessor.GetOriginalInstructions(instantiate));
        }, () =>
        {
            if (runtime != null || instantiate == null || compile == null || verifier == null || metadataReader == null || infoSetter == null)
                throw new InvalidOperationException("Mod discovery is unvalidated or already owned.");
            verify = verifier.CreateDelegate<System.Func<ModLoader, List<ModContainer>, List<ModContainer>>>();
            readInfo = metadataReader.CreateDelegate<ReadModInfo>();
            setInfo = infoSetter.CreateDelegate<System.Action<GameMod, ModInfo>>();
            runtime = owner; attempted = true;
            harmony.Patch(instantiate,
                prefix: new HarmonyMethod(typeof(ModCompatibilityConsumerPatches), nameof(BeforeInstantiate)) { priority = Priority.First },
                transpiler: new HarmonyMethod(typeof(ModCompatibilityConsumerPatches), nameof(InstantiateTranspiler)) { priority = Priority.First });
            harmony.Patch(compile,
                transpiler: new HarmonyMethod(typeof(ModCompatibilityConsumerPatches), nameof(CompileTranspiler)) { priority = Priority.First });
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner);
            if (ReferenceEquals(runtime, owner))
            {
                runtime = null; verify = null; readInfo = null; setInfo = null;
                lock (Gate) { Inspections.Clear(); Refused.Clear(); }
            }
            attempted = false;
        });
    }

    /// <summary>Resolves a single concrete instance method in the pinned official profile.</summary>
    /// <param name="type">Official declaring type.</param>
    /// <param name="name">Exact method name.</param>
    /// <param name="result">Expected return type.</param>
    /// <param name="parameters">Complete parameter signature.</param>
    /// <returns>Verified method or a startup-profile error.</returns>
    private static MethodInfo Method(Type type, string name, Type result, params Type[] parameters)
    {
        MethodInfo method = AccessTools.DeclaredMethod(type, name, parameters) ?? throw new MissingMethodException(type.FullName, name);
        if (method.IsStatic || method.ReturnType != result || method.GetMethodBody() == null)
            throw new InvalidOperationException("Official mod-loading signature changed: " + type.FullName + "." + name);
        return method;
    }

    /// <summary>Inspects enabled client/universal code-mod files after normal disablement, dependency ordering and unpacking.</summary>
    /// <param name="__instance">Original loader retaining its ordinary dependency algorithm.</param>
    /// <param name="__0">Original instantiate list, replaced with the game's reverified result after a refusal.</param>
    /// <param name="___side">Original loader side; integrated/dedicated server loading is left intact.</param>
    private static void BeforeInstantiate(ModLoader __instance, ref List<ModContainer> __0, EnumAppSide ___side)
    {
        if (runtime?.IsActive != true || ___side != EnumAppSide.Client) return;
        bool rejected = false;
        foreach (var mod in __0.Where(mod => ShouldInspect(mod, mod.Info)))
        {
            foreach (string path in mod.AssemblyFiles)
            {
                ModGlInspection result;
                try { result = ModGlUsage.Inspect(File.ReadAllBytes(path), path, mod.Info); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { result = new(mod.Info, path, "unreadable", Guid.Empty, "unavailable", [], error.Message); }
                Record(result);
                if (result.Refused) { Refuse(mod, result); rejected = true; }
            }
        }
        if (rejected) __0 = verify!(__instance, __0);
    }

    /// <summary>Checks the emitted source image immediately before the original Assembly.Load(byte[]) call.</summary>
    /// <param name="image">Exact Roslyn output bytes the game would load.</param>
    /// <param name="mod">Original compiling mod container.</param>
    /// <returns>The original assembly load result when no unsupported operation is found.</returns>
    /// <remarks>Source-only mods can compile during metadata discovery before DisableMods. The game's metadata reader supplies identity for saved-disablement checks and precise refusal diagnostics.</remarks>
    private static Assembly LoadCheckedSource(byte[] image, ModContainer mod)
    {
        if (runtime?.IsActive != true || !mod.Enabled) return Assembly.Load(image);
        ModInfo? originalInfo = mod.Info;
        ModGlInspection result = ModGlUsage.Inspect(image, mod.SourcePath + " [emitted source]", originalInfo,
            assembly => readInfo!(mod, assembly, out _));
        if (!ShouldInspect(mod, result.Info)) return Assembly.Load(image);
        Record(result);
        if (!result.Refused) return Assembly.Load(image);
        // Preserve the official identity when compilation preceded Info assignment,
        // so the game's existing catch, DisableMods and dependency paths still work.
        if (originalInfo == null && result.Info != null) setInfo!(mod, result.Info);
        Refuse(mod, result);
        throw new NotSupportedException("VulkanStory cannot initialize this source mod: " + result.Detail);
    }

    /// <summary>Applies bounded client-side policy without modifying the game's saved disabled-mod configuration.</summary>
    /// <param name="mod">Original container with normal enabled/error state.</param>
    /// <param name="info">Original or metadata-derived mod info.</param>
    /// <returns>True for enabled third-party client/universal code; false for official, VulkanStory, disabled or server-only mods.</returns>
    private static bool ShouldInspect(ModContainer mod, ModInfo? info)
    {
        if (!mod.Enabled || info?.Side == EnumAppSide.Server || (info != null && info.Type != EnumModType.Code)) return false;
        if (info?.ModID is "vulkanstory" or "vulkanstoryinput") return false;
        string source = Path.GetFullPath(mod.SourcePath);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (string builtin in new[] { "VSEssentials.dll", "VSSurvivalMod.dll", "VSCreativeMod.dll" })
            if (string.Equals(source, Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Mods", builtin)), comparison)) return false;
        if (info != null && ClientSettings.DisabledMods is { } disabled &&
            (disabled.Contains(info.ModID) || disabled.Contains(info.ModID + "@" + info.Version))) return false;
        return true;
    }

    /// <summary>Stores one latest decision per assembly source for the ordinary status command.</summary>
    /// <param name="result">Metadata-only image decision.</param>
    private static void Record(ModGlInspection result)
    {
        lock (Gate) Inspections[result.Source] = result;
        runtime?.Notify("mod.gl.discovery", result.Detail);
    }

    /// <summary>Uses the game's loading error to prevent initialization and logs how to obtain a supported profile.</summary>
    /// <param name="mod">Original enabled mod receiving its transient load error.</param>
    /// <param name="result">Precise inspected-image and operation evidence.</param>
    private static void Refuse(ModContainer mod, ModGlInspection result)
    {
        mod.SetError(ModError.Loading);
        lock (Gate) Refused.Add(mod);
        mod.Logger.Error("VulkanStory: " + result.Detail +
            "\nThis mod needs a version-specific GL adapter. Provide its ZIP/DLL, dependencies, shaders and a representative scene. " +
            "The renderer remains Vulkan; use the mod manager to disable this mod or restart with VulkanStory disabled.");
    }

    /// <summary>Reapplies original dependency errors after source refusals before any mod-system constructors execute.</summary>
    /// <param name="loader">Original loader.</param>
    /// <param name="mods">Original list; its active enumerator has not yet been created.</param>
    private static void RecheckAfterLoads(ModLoader loader, List<ModContainer> mods)
    {
        if (runtime?.IsActive != true) return;
        bool rejected;
        lock (Gate) rejected = mods.Any(Refused.Contains);
        // verifyMods sets dependency errors itself. Keep this list unchanged so
        // errored containers remain observable, and the original Enabled guards skip them.
        if (rejected) _ = verify!(loader, mods);
    }

    /// <summary>Requires the sole emitted-byte load anchor in the official source compiler.</summary>
    /// <param name="instructions">Original or incoming source compiler IL.</param>
    private static void CheckCompile(IEnumerable<CodeInstruction> instructions)
    {
        if (instructions.Count(instruction => instruction.Calls(SourceLoad)) != 1)
            throw new InvalidOperationException("Expected exactly one Assembly.Load(byte[]) source-mod anchor.");
    }

    /// <summary>Exact byte-array load overload at official source compiler IL_01da.</summary>
    private static MethodInfo SourceLoad => typeof(Assembly).GetMethod(nameof(Assembly.Load), [typeof(byte[])])!;

    /// <summary>Locates the checked second mod-list enumeration after all assembly loading and before mod-system construction.</summary>
    /// <param name="instructions">Original or incoming instantiateMods IL.</param>
    /// <returns>Index of the list receiver instruction to precede with dependency recalculation.</returns>
    private static int RecheckBoundary(IReadOnlyList<CodeInstruction> instructions)
    {
        MethodInfo enumerate = typeof(List<ModContainer>).GetMethod(nameof(List<ModContainer>.GetEnumerator), Type.EmptyTypes)!;
        MethodInfo load = AccessTools.Method(typeof(ModContainer), nameof(ModContainer.LoadAssembly), [typeof(ModCompilationContext), typeof(ModAssemblyLoader)])!;
        MethodInfo construct = AccessTools.Method(typeof(ModContainer), nameof(ModContainer.InstantiateModSystems), [typeof(EnumAppSide)])!;
        int[] enumerations = Enumerable.Range(0, instructions.Count).Where(index => instructions[index].Calls(enumerate)).ToArray();
        int[] loads = Enumerable.Range(0, instructions.Count).Where(index => instructions[index].Calls(load)).ToArray();
        int[] constructors = Enumerable.Range(0, instructions.Count).Where(index => instructions[index].Calls(construct)).ToArray();
        if (enumerations.Length != 2 || loads.Length != 1 || constructors.Length != 1 ||
            enumerations[0] >= loads[0] || loads[0] >= enumerations[1] || enumerations[1] >= constructors[0] ||
            instructions[enumerations[1] - 1].opcode != OpCodes.Ldarg_1)
            throw new InvalidOperationException("Original mod load/constructor enumeration anchors changed.");
        return enumerations[1] - 1;
    }

    /// <summary>Replaces only the compiler's sole assembly load, passing its original mod argument.</summary>
    /// <param name="instructions">Incoming source compiler IL.</param>
    /// <returns>Checked IL retaining original control-flow and exception boundaries.</returns>
    private static IEnumerable<CodeInstruction> CompileTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList(); CheckCompile(body);
        foreach (var instruction in body)
        {
            if (instruction.Calls(SourceLoad))
            {
                var argument = new CodeInstruction(OpCodes.Ldarg_1);
                MoveStart(instruction, argument);
                yield return argument;
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(ModCompatibilityConsumerPatches), nameof(LoadCheckedSource));
            }
            yield return instruction;
        }
    }

    /// <summary>Inserts dependency recalculation at the checked post-load boundary.</summary>
    /// <param name="instructions">Incoming instantiateMods IL.</param>
    /// <returns>Original loader IL with one pre-constructor dependency callback.</returns>
    private static IEnumerable<CodeInstruction> InstantiateTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList(); int boundary = RecheckBoundary(body);
        for (int index = 0; index < body.Count; index++)
        {
            if (index == boundary)
            {
                var receiver = new CodeInstruction(OpCodes.Ldarg_0);
                MoveStart(body[index], receiver);
                yield return receiver;
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ModCompatibilityConsumerPatches), nameof(RecheckAfterLoads)));
            }
            yield return body[index];
        }
    }

    /// <summary>Transfers incoming branch targets and exception starts to the first inserted instruction.</summary>
    /// <param name="original">Instruction being preceded or substituted.</param>
    /// <param name="first">First instruction that must execute on every incoming path.</param>
    private static void MoveStart(CodeInstruction original, CodeInstruction first)
    {
        first.labels.AddRange(original.labels); original.labels.Clear();
        first.blocks.AddRange(original.blocks.Where(block => block.blockType != ExceptionBlockType.EndExceptionBlock));
        original.blocks.RemoveAll(block => block.blockType != ExceptionBlockType.EndExceptionBlock);
    }
}
