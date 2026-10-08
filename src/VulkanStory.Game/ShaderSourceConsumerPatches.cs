using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

// Retained shader files pass through the original include/prefix/compile lifecycle.
/// <summary>Supplies retained shader stages and includes only when the asset override policy permits replacement.</summary>
/// <remarks>Patch discovery and installation belong to startup. Callbacks use the committed routing predicate; game object identity remains in the integration assembly.</remarks>
internal static class ShaderSourceConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-shader-sources";
    private static ProcessRuntime? runtime;
    private static readonly AccessTools.FieldRef<Shader, string> Filename =
        AccessTools.FieldRefAccess<Shader, string>("Filename");
    private static readonly MethodInfo Load = AccessTools.Method(typeof(ShaderRegistry), "LoadShader",
        [typeof(ShaderProgram), typeof(EnumShaderType)])!;
    private static readonly MethodInfo Prefixes = AccessTools.Method(typeof(ShaderRegistry), "registerDefaultShaderCodePrefixes",
        [typeof(ShaderProgram), typeof(bool)])!;
    private static readonly MethodInfo Include = AccessTools.Method(typeof(ShaderRegistry), "InsertIncludedFile",
        [typeof(ShaderProgram), typeof(string), typeof(HashSet<string>)])!;
    private static readonly MethodInfo Registered = AccessTools.Method(typeof(ShaderRegistry), "loadRegisteredShaderPrograms", [])!;
    private static readonly Func<ShaderProgram, string, HashSet<string>, string> Expand =
        AccessTools.Method(typeof(ShaderRegistry), "HandleIncludes",
            [typeof(ShaderProgram), typeof(string), typeof(HashSet<string>)])!
        .CreateDelegate<Func<ShaderProgram, string, HashSet<string>, string>>();

    /// <summary>Creates the dormant patch group for this consumer path; validation and installation remain separate transaction steps.</summary>
    /// <param name="owner">Process runtime that owns this group and its session.</param>
    /// <returns>Validation, installation and removal callbacks for the startup transaction.</returns>
    /// <remarks>Binding or IL-anchor mismatches reject the group. Creating the group does not enable graphics routing.</remarks>
    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner)
    {
        var harmony = new Harmony(Owner); bool attempted = false;
        return new StartupPatchGroup("graphics-shader-sources", () =>
        {
            if (AccessTools.Field(typeof(Shader), "Filename")?.FieldType != typeof(string))
                throw new MissingFieldException("Original shader filename storage changed.");
            foreach (var method in new[] { Load, Prefixes, Include, Registered })
                if (method == null || !method.IsStatic || method.GetMethodBody() == null)
                    throw new MissingMethodException("Original shader source lifecycle changed.");
            if (Load.ReturnType != typeof(void) || Prefixes.ReturnType != typeof(void) ||
                Include.ReturnType != typeof(string) || Registered.ReturnType != typeof(bool))
                throw new InvalidOperationException("Original shader lifecycle return types changed.");
        }, () =>
        {
            if (runtime != null) throw new InvalidOperationException("Shader source routing already has an owner.");
            runtime = owner; attempted = true;
            harmony.Patch(Load, prefix: new HarmonyMethod(typeof(ShaderSourceConsumerPatches), nameof(LoadSource)) { priority = Priority.First });
            harmony.Patch(Include, prefix: new HarmonyMethod(typeof(ShaderSourceConsumerPatches), nameof(LoadInclude)) { priority = Priority.First });
            harmony.Patch(Prefixes, postfix: new HarmonyMethod(typeof(ShaderSourceConsumerPatches), nameof(DefineModes)) { priority = Priority.Last });
            harmony.Patch(Registered, prefix: new HarmonyMethod(typeof(ShaderSourceConsumerPatches), nameof(BeginLoad)),
                postfix: new HarmonyMethod(typeof(ShaderSourceConsumerPatches), nameof(EndLoad)),
                finalizer: new HarmonyMethod(typeof(ShaderSourceConsumerPatches), nameof(FailedLoad)));
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner); if (ReferenceEquals(runtime, owner)) runtime = null;
            attempted = false;
        });
    }
    private static GameGraphicsAdapter? Adapter()
    {
        if (runtime?.Routing.RoutingEnabled != true) return null;
        if (ScreenManager.Platform is not ClientPlatformWindows platform || !runtime.TrySession(platform, out var session))
            throw new InvalidOperationException("Active shader source routing lost its session.");
        return session.Graphics;
    }
    private static string? Source(string name)
    {
        using Stream? stream = typeof(GameGraphicsAdapter).Assembly.GetManifestResourceStream("VulkanStory.Game.Shaders." + name);
        if (stream == null) return null;
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    private static bool LoadSource(ShaderProgram __0, EnumShaderType __1)
    {
        if (Adapter() is not { } graphics) return true;
        string? extension = __1 switch { EnumShaderType.VertexShader => "vsh", EnumShaderType.FragmentShader => "fsh", _ => null };
        if (extension == null) return true;
        if (!graphics.ShaderOverrides.MayReplaceStage(__0.PassName, __0.AssetDomain, extension)) return true;
        if (Source(__0.PassName + "." + extension) is not { } code) return true;
        code = Expand(__0, code, new HashSet<string>());
        Shader? stage = __1 == EnumShaderType.VertexShader ? __0.VertexShader : __0.FragmentShader;
        if (stage == null) stage = new Shader(__1, code, __0.PassName + "." + extension);
        else { stage.Code = code; stage.Type = __1; Filename(stage) = __0.PassName + "." + extension; }
        if (__1 == EnumShaderType.VertexShader) __0.VertexShader = stage; else __0.FragmentShader = stage;
        graphics.NoteRetainedSceneSource(__0, __1 == EnumShaderType.VertexShader);
        return false;
    }
    private static bool LoadInclude(ShaderProgram __0, string __1, HashSet<string>? __2, ref string __result)
    {
        if (Adapter() is not { } graphics || __1 != "vertexwarp.vsh" || Source(__1) is not { } code) return true;
        if (!graphics.ShaderOverrides.MayReplaceInclude(__1)) return true;
        __0.includes.Add(__1);
        __result = Expand(__0, code, __2 ?? new HashSet<string>());
        return false;
    }
    private static void DefineModes(ShaderProgram __0) => Adapter()?.PrepareSceneShaderModes(__0);
    private static void BeginLoad() => Adapter()?.BeginSceneShaderLoad();
    private static void EndLoad(bool __result) => Adapter()?.EndSceneShaderLoad(__result);
    private static Exception? FailedLoad(Exception? __exception)
    { if (__exception != null) Adapter()?.EndSceneShaderLoad(false); return __exception; }
}
