using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

/// <summary>Shader subset for eventual composition into the complete graphics-api group.</summary>
internal static class ShaderConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-shaders";
    private static Func<bool>? routingEnabled;
    private sealed record Binding(string Target, Type Result, Type[] Parameters, string Prefix);
    private static readonly Binding[] Profile1227 =
    [
        new("CompileShader", typeof(bool), [typeof(Shader)], nameof(Compile)),
        new("CreateShaderProgram", typeof(bool), [typeof(ShaderProgram)], nameof(Link)),
        new("GetUniformLocation", typeof(int), [typeof(ShaderProgram), typeof(string)], nameof(Location)),
    ];

    internal static void ValidateBindings()
    {
        foreach (var binding in Profile1227) Resolve(binding);
        foreach (var method in SelectionTargets()) ValidateSelection(PatchProcessor.GetOriginalInstructions(method));
        ShaderUniformRoutes.ValidateBindings();
        ShaderSamplerRoutes.ValidateBindings();
        UniformBufferRoutes.ValidateBindings();
        ShaderDisposalRoutes.ValidateBindings();
    }

    // This subset is not a complete mandatory graphics-api group and cannot
    // satisfy StartupRoutingTransaction.RequiredGroups by itself.
    internal static StartupPatchGroup CreateSubset(Func<bool> enabled)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        var harmony = new Harmony(Owner);
        (MethodInfo Original, MethodInfo Prefix)[]? methods = null;
        MethodInfo[]? selectionMethods = null;
        bool attempted = false;
        return new StartupPatchGroup("graphics-shaders", () =>
        {
            ValidateBindings();
            selectionMethods = SelectionTargets();
            methods = Profile1227.Select(binding => (Resolve(binding),
                typeof(ShaderConsumerPatches).GetMethod(binding.Prefix,
                    BindingFlags.NonPublic | BindingFlags.Static)!)).ToArray();
        }, () =>
        {
            if (methods is null) throw new InvalidOperationException("Shader targets were not validated.");
            if (routingEnabled != null) throw new InvalidOperationException("Shader routing already has an owner.");
            routingEnabled = enabled;
            attempted = true;
            // Harmony builds caller wrappers while patching. Install small leaf
            // setters first so Use/Stop cannot retain inlined original GL bodies.
            ShaderUniformRoutes.Install(harmony);
            UniformBufferRoutes.Install(harmony);
            ShaderSamplerRoutes.Install(harmony);
            ShaderDisposalRoutes.Install(harmony);
            foreach (var method in methods)
                harmony.Patch(method.Original, prefix: new HarmonyMethod(method.Prefix) { priority = Priority.First });
            foreach (var method in selectionMethods!)
                harmony.Patch(method, transpiler: new HarmonyMethod(typeof(ShaderConsumerPatches),
                    nameof(SelectionTranspiler)) { priority = Priority.First });
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner);
            UniformBufferUploads.Clear();
            if (ReferenceEquals(routingEnabled, enabled)) routingEnabled = null;
            attempted = false;
        });
    }

    private static MethodInfo Resolve(Binding binding)
    {
        MethodInfo? method = typeof(ClientPlatformWindows).GetMethod(binding.Target,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            binder: null, types: binding.Parameters, modifiers: null);
        if (method is null || method.ReturnType != binding.Result ||
            method.ContainsGenericParameters || method.GetMethodBody() is null)
            throw new MissingMethodException(typeof(ClientPlatformWindows).FullName, binding.Target);
        return method;
    }

    internal static bool TryAdapter(ClientPlatformWindows platform, out GameGraphicsAdapter adapter)
    {
        adapter = null!;
        if (routingEnabled?.Invoke() != true) return false;
        if (!GameGraphicsAdapter.TryGet(platform, out var found) || found is null)
            throw new InvalidOperationException("Active graphics routing has no renderer adapter for this platform.");
        adapter = found;
        return true;
    }

    private static bool Compile(ClientPlatformWindows __instance, Shader __0, ref bool __result)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        __result = adapter.CompileShader(__0);
        return false;
    }
    private static bool Link(ClientPlatformWindows __instance, ShaderProgram __0, ref bool __result)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        __result = adapter.CreateShaderProgram(__0);
        return false;
    }
    private static bool Location(ClientPlatformWindows __instance, ShaderProgram __0, string __1, ref int __result)
    {
        if (!TryAdapter(__instance, out var adapter)) return true;
        __result = adapter.GetUniformLocation(__0, __1);
        return false;
    }
    private static MethodInfo[] SelectionTargets() => [SelectionTarget("Use"), SelectionTarget("Stop")];

    private static MethodInfo SelectionTarget(string name)
    {
        MethodInfo? method = typeof(ShaderProgramBase).GetMethod(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
            binder: null, types: Type.EmptyTypes, modifiers: null);
        if (method is null || method.ReturnType != typeof(void) || method.GetMethodBody() is null)
            throw new MissingMethodException(typeof(ShaderProgramBase).FullName, name);
        return method;
    }

    private static readonly MethodInfo GlUse = typeof(GL).GetMethod(nameof(GL.UseProgram), [typeof(int)])!;
    private static readonly MethodInfo RoutedUse = typeof(ShaderConsumerPatches).GetMethod(nameof(SelectProgram),
        BindingFlags.Static | BindingFlags.NonPublic)!;

    private static void ValidateSelection(IEnumerable<CodeInstruction> instructions)
    {
        if (instructions.Count(instruction => instruction.Calls(GlUse)) != 1)
            throw new InvalidOperationException("Official shader selection must contain exactly one GL.UseProgram(int) call.");
    }

    internal static IEnumerable<CodeInstruction> SelectionTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList();
        ValidateSelection(body); // Incoming Harmony IL is checked as well as original IL.
        foreach (var instruction in body)
        {
            if (instruction.Calls(GlUse))
            {
                // Same static int->void stack shape; labels and exception blocks remain attached.
                instruction.opcode = OpCodes.Call;
                instruction.operand = RoutedUse;
            }
        }
        return body;
    }

    private static void SelectProgram(int programId)
    {
        if (routingEnabled?.Invoke() != true) { GL.UseProgram(programId); return; }
        if (Vintagestory.Client.ScreenManager.Platform is not ClientPlatformWindows platform)
            throw new InvalidOperationException("Active shader selection has no original platform.");
        if (!TryAdapter(platform, out var adapter))
            throw new InvalidOperationException("Shader selection routing changed during dispatch.");
        adapter.UseShaderProgram(programId);
    }

    internal static bool TryActiveShaderAdapter(out GameGraphicsAdapter adapter)
    {
        adapter = null!;
        if (routingEnabled?.Invoke() != true) return false;
        if (Vintagestory.Client.ScreenManager.Platform is not ClientPlatformWindows platform)
            throw new InvalidOperationException("Active shader uniforms have no original platform.");
        if (!TryAdapter(platform, out adapter))
            throw new InvalidOperationException("Shader uniform routing changed during dispatch.");
        return true;
    }
    internal static bool GraphicsRoutingEnabled => routingEnabled?.Invoke() == true;
}
