using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    private sealed record CompiledSceneModes(bool Motion, bool Gtao, int MotionLocation);
    private static readonly string[] sceneMotionWriters =
        ["chunkopaque", "chunktopsoil", "entityanimated", "standard", "instanced", "decals", "particlescube"];
    private ConditionalWeakTable<ShaderProgram, CompiledSceneModes> preparedSceneModes = new();
    private readonly Dictionary<string, CompiledSceneModes> linkedSceneModes = new(StringComparer.Ordinal);
    private readonly Dictionary<ShaderProgram, byte> retainedSceneSources = new();
    private bool sceneShaderLoading;
    private int compiledMotionLocation = -1;
    private ShaderOverridePolicy? shaderOverrides;
    internal ShaderOverridePolicy ShaderOverrides => shaderOverrides ?? throw new InvalidOperationException("Shader override policy is not attached.");
    internal void ConfigureShaderOverrides(ShaderOverridePolicy policy)
    {
        RequireLifecycleDevice();
        if (shaderOverrides != null) throw new InvalidOperationException("Shader override policy already has an owner.");
        shaderOverrides = policy;
    }
    internal bool CompiledMotionTargetsMatch => !sceneShaderLoading && AoTemporal.EntityMotion.Enabled &&
        compiledMotionLocation >= 0 && FrameState.MotionAttachment == compiledMotionLocation;
    internal string CompiledMotionStatus => sceneShaderLoading ? "scene shader load in progress" :
        !AoTemporal.EntityMotion.Enabled ? "compiled scene motion disabled" :
        compiledMotionLocation < 0 ? "no certified shader motion location" :
        FrameState.MotionAttachment != compiledMotionLocation
            ? "shader motion location " + compiledMotionLocation + " differs from target " + FrameState.MotionAttachment
            : "shader motion location " + compiledMotionLocation + " matches target";
    private CompiledSceneModes RequestedSceneModes() => new(AoSettings.EffectiveTemporalPipeline && ShaderOverrides.AllowsRetainedSceneFeatures,
        (AoSettings.Settings.AmbientOcclusion == "gtao" ||
            (AoSettings.Settings.AmbientOcclusion == "auto" && AoSettings.EffectiveTemporalPipeline)) && ClientSettings.SSAOQuality > 0 && ShaderOverrides.AllowsRetainedSceneFeatures,
        ClientSettings.SSAOQuality > 0 ? 4 : 2);

    private static string Define(string? prefix, string name, int value)
    {
        string clean = Regex.Replace(prefix ?? "", @"^[ \t]*#define[ \t]+" + Regex.Escape(name) + @"\b[^\r\n]*(?:\r?\n|$)", "", RegexOptions.Multiline);
        return clean + "\n#define " + name + " " + value + "\n";
    }
    internal void PrepareSceneShaderModes(ShaderProgram program)
    {
        RequireDevice();
        if (!program.LoadFromFile || (!string.IsNullOrEmpty(program.AssetDomain) &&
            !string.Equals(program.AssetDomain, "game", StringComparison.OrdinalIgnoreCase)))
            ShaderOverrides.MarkProgram(program.PassName);
        CompiledSceneModes mode = RequestedSceneModes();
        preparedSceneModes.Remove(program); preparedSceneModes.Add(program, mode);
        foreach (Shader? stage in new[] { program.VertexShader, program.FragmentShader })
        {
            if (stage == null) continue;
            stage.PrefixCode = Define(stage.PrefixCode, "TAAMOTION", mode.Motion ? 1 : 0);
            stage.PrefixCode = Define(stage.PrefixCode, "TAAMOTIONLOCATION", mode.MotionLocation);
            stage.PrefixCode = Define(stage.PrefixCode, "OPTIMUMAO", mode.Gtao ? 1 : 0);
            // Greedy meshing is an unrelated donor optimization; the port uses original meshes.
            stage.PrefixCode = Define(stage.PrefixCode, "GREEDYMESH", 0);
            stage.PrefixCode = Define(stage.PrefixCode, "GREEDYMESH_GRAD", 0);
        }
        if (program.FragmentShader != null)
            program.FragmentShader.PrefixCode = Define(program.FragmentShader.PrefixCode, "FXAA",
                ClientSettings.FXAA && AoSettings.Settings.RenderScale >= 1f && !mode.Motion ? 1 : 0);
    }
    internal void BeginSceneShaderLoad()
    {
        RequireDevice();
        if (sceneShaderLoading) throw new InvalidOperationException("Recursive registered shader loading.");
        sceneShaderLoading = true; linkedSceneModes.Clear(); retainedSceneSources.Clear(); preparedSceneModes = new();
        ShaderOverrides.Refresh();
        compiledMotionLocation = -1;
        AoTemporal.SetMotionShaderMode(false); SetAmbientOcclusionShaderMode(false);
    }
    private void SceneShaderLinked(ShaderProgram program)
    {
        if (sceneShaderLoading && retainedSceneSources.GetValueOrDefault(program) == 3 &&
            preparedSceneModes.TryGetValue(program, out var mode))
        {
            if (mode.Motion && sceneMotionWriters.Contains(program.PassName) &&
                !RequireDevice().NativeProgramWritesOutput(program.ProgramId, mode.MotionLocation))
            {
                platform!.Logger.Error("VulkanStory: motion shader '{0}' does not write attachment {1}; scene motion remains disabled.",
                    program.PassName, mode.MotionLocation);
                mode = mode with { Motion = false };
            }
            linkedSceneModes[program.PassName] = mode;
        }
    }
    internal void NoteRetainedSceneSource(ShaderProgram program, bool vertex) =>
        retainedSceneSources[program] = (byte)(retainedSceneSources.GetValueOrDefault(program) | (vertex ? 1 : 2));
    /// <summary>Publishes motion/AO readiness after the original shader load completes; failed or incompatible compiled modes disable the corresponding features.</summary>
    /// <param name="success">Whether the original registered-program load completed successfully.</param>
    internal void EndSceneShaderLoad(bool success)
    {
        RequireDevice();
        if (!sceneShaderLoading) return;
        sceneShaderLoading = false;
        CompiledSceneModes requested = RequestedSceneModes();
        bool complete = success && sceneMotionWriters.All(name => linkedSceneModes.TryGetValue(name, out var mode) && mode == requested);
        AoTemporal.SetMotionShaderMode(complete && requested.Motion);
        compiledMotionLocation = complete && requested.Motion ? requested.MotionLocation : -1;
        SetAmbientOcclusionShaderMode(complete && requested.Gtao);
        if (!complete)
        {
            string[] mismatched = sceneMotionWriters.Where(name =>
                !linkedSceneModes.TryGetValue(name, out var mode) || mode != requested).ToArray();
            platform!.Logger.Warning("VulkanStory: scene shader modes remain disabled after incomplete shader loading; load success={0}, requested motion={1}, location={2}, GTAO={3}, missing/mismatched writers={4}.",
                success, requested.Motion, requested.MotionLocation, requested.Gtao, string.Join(",", mismatched));
        }
        // Shader compilation certifies modes, not complete per-frame motion coverage.
        AoTemporal.PublishMotionCoverage(false);
        retainedSceneSources.Clear();
    }
}
