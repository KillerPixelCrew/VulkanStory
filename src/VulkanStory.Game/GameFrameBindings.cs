using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

/// <summary>Caches original platform render-option field delegates and dispatches its unchanged per-frame method.</summary>
internal static class GameFrameBindings
{
    private static readonly AccessTools.FieldRef<ClientPlatformWindows, NewFrameHandler?> Handler =
        AccessTools.FieldRefAccess<ClientPlatformWindows, NewFrameHandler?>("frameHandler");
    private static readonly AccessTools.FieldRef<ClientPlatformWindows, bool> Bloom = AccessTools.FieldRefAccess<ClientPlatformWindows, bool>("RenderBloom");
    private static readonly AccessTools.FieldRef<ClientPlatformWindows, bool> GodRays = AccessTools.FieldRefAccess<ClientPlatformWindows, bool>("RenderGodRays");
    private static readonly AccessTools.FieldRef<ClientPlatformWindows, bool> Fxaa = AccessTools.FieldRefAccess<ClientPlatformWindows, bool>("RenderFXAA");
    private static readonly AccessTools.FieldRef<ClientPlatformWindows, bool> Ssao = AccessTools.FieldRefAccess<ClientPlatformWindows, bool>("RenderSSAO");
    private static readonly AccessTools.FieldRef<ClientPlatformWindows, int> Shadow = AccessTools.FieldRefAccess<ClientPlatformWindows, int>("ShadowMapQuality");
    internal static void Validate()
    {
        foreach (var (name, type) in new (string, Type)[]
        { ("frameHandler", typeof(NewFrameHandler)), ("RenderBloom", typeof(bool)), ("RenderGodRays", typeof(bool)),
          ("RenderFXAA", typeof(bool)), ("RenderSSAO", typeof(bool)), ("ShadowMapQuality", typeof(int)) })
            if (AccessTools.Field(typeof(ClientPlatformWindows), name)?.FieldType != type)
                throw new MissingFieldException("Original frame metadata changed: " + name);
    }
    /// <summary>Publishes the pre-input render-option snapshot to the original platform fields.</summary>
    /// <param name="platform">Original platform whose render callback consumes the values.</param>
    /// <param name="settings">Single frame settings snapshot.</param>
    internal static void Adopt(ClientPlatformWindows platform, GameFrameSettings settings)
    {
        Bloom(platform) = settings.Bloom; GodRays(platform) = settings.GodRays;
        Fxaa(platform) = settings.Fxaa; Ssao(platform) = settings.Ssao;
        Shadow(platform) = settings.ShadowQuality;
        ShaderProgramBase.shadowmapQuality = settings.ShadowQuality;
    }
    /// <summary>Invokes the unchanged original platform per-frame renderer.</summary>
    /// <param name="platform">Original platform owned by the active session.</param>
    /// <param name="delta">Elapsed real-frame time in seconds.</param>
    internal static void Dispatch(ClientPlatformWindows platform, float delta) =>
        (Handler(platform) ?? throw new InvalidOperationException("Original game frame handler is not attached.")).OnNewFrame(delta);
    internal static bool RenderBloom(ClientPlatformWindows platform) => Bloom(platform);
    internal static bool RenderGodRays(ClientPlatformWindows platform) => GodRays(platform);
    internal static bool RenderFxaa(ClientPlatformWindows platform) => Fxaa(platform);
    internal static bool RenderSsao(ClientPlatformWindows platform) => Ssao(platform);
}
