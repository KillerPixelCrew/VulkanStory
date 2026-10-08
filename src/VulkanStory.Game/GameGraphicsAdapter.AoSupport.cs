using Vintagestory.Client.NoObf;
using Vintagestory.API.Config;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    private GameTemporalOwner? aoTemporal;
    private RendererSettingsState AoSettings => postSettings ?? throw new InvalidOperationException("AO settings are not attached.");
    private GameTemporalOwner AoTemporal => aoTemporal ?? throw new InvalidOperationException("AO temporal owner is not attached.");
    internal bool AmbientOcclusionInScene { get; private set; }
    internal int AmbientOcclusionTexture { get; private set; }
    internal bool AmbientOcclusionShadersUseGtao { get; private set; }
    private int sceneSsaoProgram;
    private bool sceneSsaoFailed;
    /// <summary>Binds the borrowed temporal owner used by AO camera inputs before scene rendering starts.</summary>
    /// <param name="temporal">Borrowed session temporal owner supplying current camera/reset state.</param>
    internal void ConfigureAmbientOcclusion(GameTemporalOwner temporal)
    {
        if (Environment.CurrentManagedThreadId != ownerThread || device == null)
            throw new InvalidOperationException("AO configuration requires the session owner thread.");
        ArgumentNullException.ThrowIfNull(temporal);
        if (aoTemporal != null) throw new InvalidOperationException("AO already has a temporal owner.");
        aoTemporal = temporal;
    }
    // Publish only after scene shaders are rebuilt with the matching G-buffer
    // class-channel and composite define; settings alone do not certify that.
    internal void SetAmbientOcclusionShaderMode(bool gtao)
    {
        RequireDevice();
        if (AmbientOcclusionShadersUseGtao == gtao) return;
        ReloadAmbientOcclusionProgram();
        AmbientOcclusionShadersUseGtao = gtao;
        AoTemporal.RequestReset();
    }
    private int SceneSsaoProgram() => OwnedProgram("scene-ssao", ref sceneSsaoProgram, ref sceneSsaoFailed,
        "#define OPTIMUMAO " + (AmbientOcclusionShadersUseGtao ? "1" : "0") +
        "\n#define OPTIMUMAO_MULTIBOUNCE 0\n#define SSAOLEVEL " + ClientSettings.SSAOQuality + "\n");
    internal void ReloadAmbientOcclusionProgram()
    {
        var renderer = RequireDevice();
        if (sceneSsaoProgram > 0) renderer.DeleteProgram(sceneSsaoProgram);
        sceneSsaoProgram = 0; sceneSsaoFailed = false; nativeSceneSsao.Pipeline = null;
        AmbientOcclusionInScene = false; AmbientOcclusionTexture = 0;
    }
    private void WriteNativeFloats(VulkanStory.Render.Vulkan.NativePipeline pipeline,
        VulkanStory.Render.Vulkan.NativeUniform uniform, float[] values) => WriteNativeMatrix(pipeline, uniform, values);
}
