using System.Runtime.InteropServices;
using System.Text.Json;
using Vintagestory.API.Client;

namespace VulkanStory.Game;

internal sealed partial class GameRenderSession
{
    /// <summary>Reads the complete display framebuffer using the shared capture boundary and a bounded pin lifetime.</summary>
    /// <returns>Display-pixel dimensions and a newly allocated, tightly packed four-channel BGRA readback in the backend's capture row order.</returns>
    /// <remarks>
    /// Runs at the session capture seam on the owner thread. Selects the default framebuffer,
    /// uses display dimensions rather than the internal SR render dimensions, and frees the
    /// managed pin even if readback fails. Framebuffer selection remains at Default afterward.
    /// Allocation overflow and backend readback failures propagate to the harness owner.
    /// </remarks>
    private (int Width, int Height, byte[] Pixels) ReadCapturedFrame()
    {
        Graphics.LoadFramebuffer(EnumFrameBuffer.Default);
        var size = Graphics.CaptureDisplaySize();
        byte[] pixels = new byte[checked(size.Width * size.Height * 4)];
        GCHandle pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { Graphics.ReadCapturePixels(0, 0, size.Width, size.Height, pin.AddrOfPinnedObject()); }
        finally { pin.Free(); }
        return (size.Width, size.Height, pixels);
    }

    /// <summary>Writes matching PNG/PPM files for one legacy whole-frame capture.</summary>
    /// <param name="frame">Zero-based world or explicitly selected main-menu frame used by the shared capture filename.</param>
    /// <returns>True after both files are written; false when the Netpbm writer declines the capture.</returns>
    /// <remarks>
    /// Both files use the same display-sized BGRA readback. PPM strips alpha and preserves
    /// capture row order for comparisons; PNG reverses rows for top-down viewing and encodes
    /// opaque alpha. File/encoding failures propagate. A PNG failure can leave an already
    /// written PPM, so the return value certifies the paired write only after PNG completes.
    /// </remarks>
    private bool WriteCapturedFrame(long frame)
    {
        var captured = ReadCapturedFrame();
        if (!HeadlessHarnessOptions.WriteFrame(frame, captured.Width, captured.Height, captured.Pixels, true)) return false;
        WriteHeadlessPng(frame, captured.Width, captured.Height, captured.Pixels);
        return true;
    }

    /// <summary>Checks requested, persisted and applied TAA through one Options diagnostic contract.</summary>
    /// <param name="expected">TAA request expected after the tested Save or Cancel action.</param>
    /// <param name="action">Options action name included in the mismatch error.</param>
    /// <returns>The matching requested JSON value, persisted file value and applied session settings value.</returns>
    /// <remarks>
    /// Runs after the diagnostic's frame boundary, when queued settings have been applied.
    /// Acceptance compares the Taa setting in all three owners; it does not certify effective
    /// TAA target readiness, a completed resolve, provider selection or visible image quality.
    /// Settings JSON and file failures propagate without manufacturing a successful result.
    /// </remarks>
    /// <exception cref="InvalidOperationException">At least one owner differs from the expected TAA setting.</exception>
    private (bool Requested, bool Persisted, bool Applied) CheckCapturedTaa(bool expected, string action)
    {
        bool requested = JsonSerializer.Deserialize<RendererSettings>(RuntimeBootstrap.Current.ReadSettings())!.Taa;
        bool persisted = new RendererSettingsStore(services.DataPath).Load().Taa;
        bool applied = services.RendererSettings.Settings.Taa;
        if (requested != expected || persisted != expected || applied != expected)
            throw new InvalidOperationException("Options " + action + " did not preserve the expected TAA state.");
        return (requested, persisted, applied);
    }
}
