using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using VulkanStory.Render.Vulkan;

namespace VulkanStory.Game;

/// <summary>Borrowed renderer association; the process session owns device lifetime.</summary>
internal sealed partial class GameGraphicsAdapter : IDisposable
{
    private static readonly ConditionalWeakTable<ClientPlatformWindows, GameGraphicsAdapter> Adapters = new();
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly Func<bool> routingEnabled;
    private readonly Func<bool> mipmapsEnabled;
    private readonly Func<int> mipmapLevel;
    private readonly Func<bool> errorChecking;
    private readonly GameShaderCallbacks shaderCallbacks;
    private ClientPlatformWindows? platform;
    private VulkanDevice? device;

    private GameGraphicsAdapter(ClientPlatformWindows platform, VulkanDevice device,
        Func<bool> routingEnabled, Func<bool> mipmapsEnabled, Func<int> mipmapLevel, Func<bool> errorChecking,
        GameShaderCallbacks shaderCallbacks)
    {
        this.platform = platform;
        this.device = device;
        this.routingEnabled = routingEnabled;
        this.mipmapsEnabled = mipmapsEnabled;
        this.mipmapLevel = mipmapLevel;
        this.errorChecking = errorChecking;
        this.shaderCallbacks = shaderCallbacks;
    }

    internal static GameGraphicsAdapter Attach(ClientPlatformWindows platform, VulkanDevice device,
        Func<bool> routingEnabled, Func<bool> mipmapsEnabled, Func<int> mipmapLevel, Func<bool> errorChecking,
        GameShaderCallbacks shaderCallbacks)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(routingEnabled);
        ArgumentNullException.ThrowIfNull(mipmapsEnabled);
        ArgumentNullException.ThrowIfNull(mipmapLevel);
        ArgumentNullException.ThrowIfNull(errorChecking);
        ArgumentNullException.ThrowIfNull(shaderCallbacks);
        ArgumentNullException.ThrowIfNull(shaderCallbacks.HandheldShadowTier);
        ArgumentNullException.ThrowIfNull(shaderCallbacks.LinkError);
        ArgumentNullException.ThrowIfNull(shaderCallbacks.ProgramLoaded);
        var adapter = new GameGraphicsAdapter(platform, device, routingEnabled, mipmapsEnabled, mipmapLevel, errorChecking, shaderCallbacks);
        Adapters.Add(platform, adapter);
        adapter.InstallModPassHooks();
        return adapter;
    }

    internal static bool TryGet(ClientPlatformWindows platform, out GameGraphicsAdapter? adapter) =>
        Adapters.TryGetValue(platform, out adapter);

    private VulkanDevice RequireDevice()
    {
        VulkanDevice owned = RequireLifecycleDevice();
        if (!routingEnabled()) throw new InvalidOperationException("Graphics routing is dormant.");
        return owned;
    }

    // Configuration and owned-resource destruction also run before commit or
    // after routing stops. This accessor never authorizes game draw dispatch.
    private VulkanDevice RequireLifecycleDevice()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("Graphics routing requires the session owner thread.");
        return device ?? throw new ObjectDisposedException(nameof(GameGraphicsAdapter));
    }

    // Retained VulkanClientPlatform.Textures bodies; GL tokens stay sampler data.
    internal void GenTexture(RawTexture texture)
    {
        VulkanDevice renderer = RequireDevice();
        int id = renderer.CreateTexture2D(texture.Width, texture.Height,
            GameTextureDefinitions.Internal(texture.PixelInternalFormat),
            GameTextureDefinitions.Pixels(texture.PixelFormat), IntPtr.Zero, false);
        renderer.SetTextureParameter(id, 10241, (int)texture.MinFilter);
        renderer.SetTextureParameter(id, 10240, (int)texture.MagFilter);
        renderer.SetTextureParameter(id, 10242, (int)texture.WrapS);
        renderer.SetTextureParameter(id, 10243, (int)texture.WrapT);
        texture.TextureId = id;
    }

    internal void BuildMipMaps(int id)
    {
        VulkanDevice renderer = RequireDevice();
        if (!mipmapsEnabled()) return;
        renderer.GenerateMipmaps(id);
        renderer.SetTextureParameter(id, 10241, 9986);
        renderer.SetTextureParameter(id, 33085, mipmapLevel());
    }

    internal void DeleteTexture(int id) => RequireDevice().DeleteTexture(id);

    private void CheckGraphicsError(string message)
    {
        if (!errorChecking()) return;
        string? error = RequireDevice().GetError();
        if (error != null)
            throw new Exception(message + " - the graphics backend reported: " + error);
    }

    public void Dispose()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("Graphics detachment requires the session owner thread.");
        if (platform is null) return;
        // Session disables routing and drains resources before detaching this bridge.
        if (routingEnabled()) throw new InvalidOperationException("Disable graphics routing before detachment.");
        DetachCaptureService();
        Adapters.Remove(platform);
        RemoveModPassHooks();
        Input.ControllerPromptFont.Release();
        framebufferHost = null;
        postSettings = null;
        aoTemporal = null;
        queries.Clear();
        ResetFramebufferPublication();
        platform = null;
        device = null;
    }
}
