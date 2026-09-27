namespace Vintagestory.API.Client;

/// <summary>
/// Cross-assembly SDL window entry point for the Vulkan client. The donor client
/// references only this contract; the renderer owns the SDL implementation.
/// </summary>
public interface IOptimumSdlClientPlatform
{
    bool TryInitializeSdlWindow(string title, int width, int height, bool hidden,
        bool fullscreen, out string reason);
    int SdlPixelWidth { get; }
    int SdlPixelHeight { get; }
    void RunSdlWindow();
}
