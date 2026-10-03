using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

/// <summary>Keep the original current-target field consistent for unchanged game consumers.</summary>
internal static class GameFramebufferBindings
{
    private static readonly AccessTools.FieldRef<ClientPlatformWindows, FrameBufferRef?> Current =
        AccessTools.FieldRefAccess<ClientPlatformWindows, FrameBufferRef?>("curFb");
    private static readonly AccessTools.FieldRef<ClientPlatformWindows, float[]> Clear =
        AccessTools.FieldRefAccess<ClientPlatformWindows, float[]>("clearColor");
    private static readonly AccessTools.FieldRef<ClientPlatformWindows, bool> Offscreen =
        AccessTools.FieldRefAccess<ClientPlatformWindows, bool>("OffscreenBuffer");
    private static readonly AccessTools.FieldRef<ClientPlatformWindows, bool> SetupSsao =
        AccessTools.FieldRefAccess<ClientPlatformWindows, bool>("SetupSSAO");
    private static readonly AccessTools.FieldRef<ClientPlatformWindows, int> ShadowQuality =
        AccessTools.FieldRefAccess<ClientPlatformWindows, int>("ShadowMapQuality");
    private static readonly AccessTools.FieldRef<ClientPlatformWindows, float> Scale =
        AccessTools.FieldRefAccess<ClientPlatformWindows, float>("ssaaLevel");
    private static readonly AccessTools.FieldRef<ClientPlatformWindows, float[]> Kernel =
        AccessTools.FieldRefAccess<ClientPlatformWindows, float[]>("ssaoKernel");
    private static readonly AccessTools.FieldRef<ClientPlatformWindows, MeshRef?> Quad =
        AccessTools.FieldRefAccess<ClientPlatformWindows, MeshRef?>("screenQuad");

    internal static void Validate()
    {
        var field = typeof(ClientPlatformWindows).GetField("curFb", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.FieldType != typeof(FrameBufferRef)) throw new MissingFieldException("Original framebuffer field changed.");
        var clear = typeof(ClientPlatformWindows).GetField("clearColor", BindingFlags.Instance | BindingFlags.NonPublic);
        if (clear?.FieldType != typeof(float[])) throw new MissingFieldException("Original framebuffer clear color changed.");
        foreach (var (name, type) in new (string, Type)[]
        {
            ("OffscreenBuffer", typeof(bool)), ("SetupSSAO", typeof(bool)),
            ("ShadowMapQuality", typeof(int)), ("ssaaLevel", typeof(float)),
            ("ssaoKernel", typeof(float[])), ("screenQuad", typeof(MeshRef)),
        })
            if (typeof(ClientPlatformWindows).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType != type)
                throw new MissingFieldException("Original framebuffer metadata changed: " + name);
    }
    internal static void Set(ClientPlatformWindows platform, FrameBufferRef? target) => Current(platform) = target;
    internal static float[] ClearColor(ClientPlatformWindows platform) => Clear(platform) ??
        throw new InvalidOperationException("Original framebuffer clear color is not initialized.");
    internal static bool OffscreenEnabled(ClientPlatformWindows platform) => Offscreen(platform);
    internal static float SsaaLevel(ClientPlatformWindows platform) => Scale(platform);
    internal static void AdoptSettings(ClientPlatformWindows platform, GameFramebufferSettings settings)
    {
        SetupSsao(platform) = settings.SsaoQuality > 0;
        ShadowQuality(platform) = settings.ShadowMapQuality;
        Scale(platform) = settings.SsaaLevel;
    }
    internal static float[] SsaoKernel(ClientPlatformWindows platform) => Kernel(platform) ??
        throw new InvalidOperationException("Original SSAO kernel is not initialized.");
    internal static MeshRef? ScreenQuad(ClientPlatformWindows platform) => Quad(platform);
    internal static void SetScreenQuad(ClientPlatformWindows platform, MeshRef quad) => Quad(platform) = quad;
}
