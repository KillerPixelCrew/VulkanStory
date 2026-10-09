using System.Text.RegularExpressions;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Contracts;

namespace VulkanStory.Game;

// Retained ChunkRenderer.RenderLiquidMotion, moved out of the injected game type.
internal sealed partial class GameGraphicsAdapter
{
    private static readonly AccessTools.FieldRef<ClientMain, ChunkRenderer> LiquidChunks =
        AccessTools.FieldRefAccess<ClientMain, ChunkRenderer>("chunkRenderer");
    private static readonly AccessTools.FieldRef<ClientCoreAPI, RenderAPIGame> LiquidRenderApi =
        AccessTools.FieldRefAccess<ClientCoreAPI, RenderAPIGame>("renderapi");
    private static readonly AccessTools.FieldRef<RenderAPIBase, bool> LiquidSsbo =
        AccessTools.FieldRefAccess<RenderAPIBase, bool>("useSSBOs");
    private ShaderProgram? liquidMotionShader;
    private int liquidMotionLocation = -1;
    private bool liquidMotionFailed;

    private string ExpandLiquidIncludes(string source, HashSet<string> includes, HashSet<string> active)
    {
        return Regex.Replace(source, @"^\s*#include\s+([^\r\n]+)", match =>
        {
            string name = match.Groups[1].Value.Trim();
            if (!active.Add(name)) throw new InvalidOperationException("Recursive liquid shader include: " + name);
            try
            {
                includes.Add(name);
                // The retained includes belong to this assembly, not the game's asset map.
                string code = OwnedShaderIncludes.Contains(name) ? RetainedLiquidIncludeSource(name) :
                    (platform!.AssetManager?.TryGet(new AssetLocation("shaderincludes/" + name))?.ToText()
                    ?? throw new InvalidOperationException("Liquid shader include missing: " + name));
                return ExpandLiquidIncludes(code, includes, active);
            }
            finally { active.Remove(name); }
        }, RegexOptions.Multiline);
    }

    /// <summary>Reads an owned liquid include from the same embedded sources used by ordinary shader routing.</summary>
    private static string RetainedLiquidIncludeSource(string name) =>
        EmbeddedShaderSource(name) ?? throw new InvalidOperationException("Owned liquid shader include missing: " + name);

    private ShaderProgram? LiquidMotionProgram(int motion)
    {
        if (liquidMotionLocation != motion) { ReloadLiquidMotionProgram(); liquidMotionLocation = motion; }
        if (liquidMotionShader != null || liquidMotionFailed) return liquidMotionShader;
        var renderer = RequireDevice();
        int id = 0;
        try
        {
            var includes = new HashSet<string>(StringComparer.Ordinal);
            string vertexCode = ExpandLiquidIncludes(OwnedShaderSource("chunkliquidmotion", "vsh"), includes, new());
            string fragmentCode = ExpandLiquidIncludes(OwnedShaderSource("chunkliquidmotion", "fsh"), includes, new());
            string prefix = MotionProgramPrefix(motion);
            var vertex = new ShaderStageDefinition { Type = ShaderStageType.VertexShader, Code = vertexCode, PrefixCode = prefix };
            var fragment = new ShaderStageDefinition { Type = ShaderStageType.FragmentShader, Code = fragmentCode, PrefixCode = prefix };
            if (!renderer.CompileShader(vertex) || !renderer.CompileShader(fragment))
                throw new InvalidOperationException(renderer.GetError());
            id = renderer.LinkProgram(new ShaderProgramDefinition("chunkliquidmotion", vertex, fragment, null, null));
            if (id <= 0) throw new InvalidOperationException(renderer.GetError());
            if (!renderer.NativeProgramWritesOutput(id, motion))
                throw new InvalidOperationException("Liquid motion shader does not write attachment " + motion + ".");
            // A normal game shader identity lets existing pool origins and dimension transforms
            // use the same routed uniform API as every other terrain draw.
            var program = new ShaderProgram { ProgramId = id, PassName = "chunkliquidmotion", LoadFromFile = false };
            program.includes.UnionWith(includes);
            foreach (Match uniform in Regex.Matches(vertexCode + "\n" + fragmentCode, @"\buniform\s+\w+\s+(\w+)"))
            {
                string name = uniform.Groups[1].Value;
                program.uniformLocations[name] = GetUniformLocation(program, name);
            }
            liquidMotionShader = program;
        }
        catch (Exception error)
        {
            if (id > 0) renderer.DeleteProgram(id);
            liquidMotionFailed = true;
            platform!.Logger.Error("VulkanStory: liquid motion shader failed: {0}", error.Message);
        }
        return liquidMotionShader;
    }

    /// <summary>Replays eligible transparent liquid geometry into the motion target using the current scene camera/history snapshot.</summary>
    /// <param name="game">Current original client used to replay liquid geometry.</param>
    /// <param name="temporal">Matching session camera/history owner.</param>
    /// <returns>True when the eligible liquid motion pass completed; false when prerequisites or native setup decline.</returns>
    internal bool RenderLiquidMotion(ClientMain game, GameTemporalOwner temporal)
    {
        if (!AoSettings.EffectiveTemporalPipeline || !temporal.State.JitterActive ||
            ShaderProgramBase.CurrentShaderProgram != null || FrameState.MotionAttachment is < 0 or >= 32) return false;
        ChunkRenderer chunks = LiquidChunks(game);
        if (chunks == null || chunks.poolsByRenderPass.Length <= 4 ||
            chunks.poolsByRenderPass[4].Length < chunks.textureIds.Length) return false;
        ShaderProgram? program = LiquidMotionProgram(FrameState.MotionAttachment);
        RenderAPIGame renderApi = LiquidRenderApi(game.api);
        bool ssbo = LiquidSsbo(renderApi), pushedMatrix = false;
        if (program == null || !BeginMotionWrite(temporal, onlyMotion: true)) return false;
        try
        {
            game.GlMatrixModeModelView(); game.GlPushMatrix(); pushedMatrix = true;
            game.GlLoadMatrix(game.MainCamera.CameraMatrixOrigin);
            ToggleBlend(false, Vintagestory.API.Client.EnumBlendMode.Standard);
            Stated.DepthTest = true; Stated.DepthWrite = true; Stated.CullEnabled = false;
            Stated.DepthCompare = Silk.NET.Vulkan.CompareOp.Less;
            program.Use();
            program.UniformMatrix("projectionMatrix", game.CurrentProjectionMatrix);
            program.UniformMatrix("modelViewMatrix", game.CurrentModelViewMatrix);
            program.Uniform("taaLiquidReactive", .3f);
            program.UniformMatrix("prevProjectionMatrix", temporal.State.GetPrevProjection(EnumTemporalView.World));
            program.UniformMatrix("prevModelViewMatrix", temporal.State.PrevCameraMatrixOrigin);
            temporal.State.ApplyMotionUniforms(program);
            LiquidSsbo(renderApi) = false;
            BeginChunkPool("chunk-liquid-motion");
            try
            {
                for (int index = 0; index < chunks.textureIds.Length; index++)
                    chunks.poolsByRenderPass[4][index].Render(game.EntityPlayer.CameraPos, "origin");
            }
            finally { EndChunkPool(); }
            ScreenManager.FrameProfiler.Mark("rend3D-ret-lqmv");
            return true;
        }
        finally
        {
            LiquidSsbo(renderApi) = ssbo;
            try
            {
                // This owned program has no sampler/UBO bindings to unbind.
                ShaderProgramBase.CurrentShaderProgram = null;
                UseShaderProgram(0);
            }
            finally
            {
                try { if (pushedMatrix) { game.GlMatrixModeModelView(); game.GlPopMatrix(); } }
                finally
                {
                    try { ToggleBlend(true, Vintagestory.API.Client.EnumBlendMode.Standard); }
                    finally { EndMotionWrite(); }
                }
            }
        }
    }

    internal void ReloadLiquidMotionProgram()
    {
        if (liquidMotionShader != null) RequireDevice().DeleteProgram(liquidMotionShader.ProgramId);
        liquidMotionShader = null; liquidMotionFailed = false; liquidMotionLocation = -1;
        chunkPasses.Remove("chunkliquidmotion");
    }
    internal void ReleaseLiquidMotionProgram()
    {
        var renderer = RequireLifecycleDevice();
        if (liquidMotionShader != null) renderer.DeleteProgram(liquidMotionShader.ProgramId);
        liquidMotionShader = null; liquidMotionFailed = false; liquidMotionLocation = -1;
        chunkPasses.Remove("chunkliquidmotion");
    }
}
