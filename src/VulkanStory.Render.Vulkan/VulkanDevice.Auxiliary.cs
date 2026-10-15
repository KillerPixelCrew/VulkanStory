using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;
using VulkanStory.Platform.Sdl;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    private sealed class AuxiliaryTarget(SdlWindowHost host, Swapchain? swapchain,
        BlitPresentPath? path, AuxiliaryPresenter? presenter = null)
    {
        public readonly SdlWindowHost Host = host;
        public readonly Swapchain? Swapchain = swapchain;
        public readonly BlitPresentPath? Path = path;
        public readonly AuxiliaryPresenter? Presenter = presenter;
        public int Framebuffer;
        public int Color;
        public int Depth;
        public int Width;
        public int Height;
        public bool Vsync;
        public bool PresentQueued;
        public int DiagnosticQueued;
        public int DiagnosticPresents;
    }

    private readonly Dictionary<int, AuxiliaryTarget> _auxiliaryTargets = new();
    private int _nextAuxiliaryTarget = 1;

    /// <summary>Creates a secondary SDL window's native presentation owner on this device and frame ring.</summary>
    /// <remarks>The caller retains the window and disposes it only after deleting this target.</remarks>
    internal int CreateAuxiliaryTarget(SdlWindowHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireFrameLifetime();
        _frames.RequireResourceLifetime();
        foreach (AuxiliaryTarget existing in _auxiliaryTargets.Values)
            if (ReferenceEquals(existing.Host, host))
                throw new InvalidOperationException("The SDL window already has an auxiliary presentation owner.");

        (int width, int height) = host.PixelSize;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("An auxiliary SDL window must have a positive drawable extent at creation.");
        // Keep a single Vulkan WSI device for the SDK and RTSS. Secondary HWNDs
        // consume the same renderer's image through the existing DX12 handoff.
        AuxiliaryPresenter? presenter = OperatingSystem.IsWindows() && _context.Streamline != null
            ? new AuxiliaryPresenter(_context, host, (uint)width, (uint)height) : null;
        Swapchain? swapchain = presenter == null ? CreateAuxiliarySwapchain(host, width, height) : null;
        var target = new AuxiliaryTarget(host, swapchain,
            presenter == null ? new BlitPresentPath(_context, _textures) : null, presenter);
        try
        {
            ResizeAuxiliaryImages(target, width, height);
            int id = _nextAuxiliaryTarget++;
            _auxiliaryTargets.Add(id, target);
            return id;
        }
        catch
        {
            ReleaseAuxiliaryImages(target);
            if (presenter != null) presenter.Dispose(); else swapchain!.Dispose();
            throw;
        }
    }

    private Swapchain CreateAuxiliarySwapchain(SdlWindowHost host, int width, int height)
    {
        var windowSurface = new SdlVulkanWindowSurface(host);
        if (!windowSurface.TryCreate(_context, out SurfaceKHR surface, out string? failure))
            throw new InvalidOperationException("Auxiliary Vulkan surface creation failed: " + failure);
        // TryCreate owns the surface on entry, including every failure path.
        if (!Swapchain.TryCreate(_context, surface, (uint)width, (uint)height,
                false, _frames.Timeline, out Swapchain? swapchain, out failure, auxiliary: true))
            throw new InvalidOperationException("Auxiliary Vulkan presentation is unavailable: " + failure);
        return swapchain!;
    }

    /// <summary>Returns the current drawable framebuffer, rebuilding its ordinary attachments after a pixel-size change.</summary>
    internal int AuxiliaryFramebuffer(int id)
    {
        RequireFrameLifetime();
        AuxiliaryTarget target = RequireAuxiliaryTarget(id);
        (int width, int height) = target.Host.PixelSize;
        // A minimized drawable keeps its last usable images. Presentation is
        // parked separately until SDL supplies a positive extent again.
        if (width > 0 && height > 0 && (width != target.Width || height != target.Height))
        {
            ResizeAuxiliaryImages(target, width, height);
            target.Swapchain?.RequestRebuild((uint)width, (uint)height, target.Vsync);
        }
        return target.Framebuffer;
    }

    /// <summary>Changes this window's requested synchronization; the next acquisition rebuilds its swapchain.</summary>
    internal void SetAuxiliaryVsync(int id, bool enabled)
    {
        RequireFrameLifetime();
        AuxiliaryTarget target = RequireAuxiliaryTarget(id);
        if (target.Vsync == enabled) return;
        target.Vsync = enabled;
        (int width, int height) = target.Host.PixelSize;
        target.Swapchain?.RequestRebuild((uint)Math.Max(1, width), (uint)Math.Max(1, height), enabled);
    }

    /// <summary>Queues this window's rendered image for presentation after the shared scene submission.</summary>
    internal void QueueAuxiliaryPresent(int id)
    {
        RequireFrameLifetime();
        if (!_frameActive)
            throw new InvalidOperationException("Auxiliary presentation must be queued inside the owning Vulkan frame.");
        AuxiliaryTarget target = RequireAuxiliaryTarget(id);
        target.PresentQueued = true;
        if (AuxiliaryTracePath is { } trace && ++target.DiagnosticQueued == 10)
        {
            // Capture the library's completed draws through the existing in-frame
            // readback path, before the shared scene submission closes recording.
            EndNativePass();
            VulkanTexture texture = _textures.Get(target.Color) ?? throw new InvalidOperationException("Auxiliary color target is missing.");
            byte[] pixels = ReadBackLevel0(texture);
            bool written = TextureDump.Write(target.Color, target.Width, target.Height, false, texture.Format, pixels);
            TraceAuxiliary(trace, new { @event = "auxiliary-capture", frameId = _latencyFrameId,
                target = target.Framebuffer, texture = target.Color, width = target.Width, height = target.Height, written });
        }
    }

    /// <summary>Drains this window's swapchain and releases its ordinary render targets before the SDL window is destroyed.</summary>
    internal void DeleteAuxiliaryTarget(int id)
    {
        RequireFrameLifetime();
        if (!_auxiliaryTargets.TryGetValue(id, out AuxiliaryTarget? target)) return;
        target.Swapchain?.RequireLifetime();
        EndAuxiliaryImageUsers();
        // Keep ownership registered if checked presentation teardown fails.
        if (target.Presenter != null) target.Presenter.Dispose(); else target.Swapchain!.Dispose();
        ReleaseAuxiliaryImages(target);
        _auxiliaryTargets.Remove(id);
    }

    private AuxiliaryTarget RequireAuxiliaryTarget(int id) =>
        _auxiliaryTargets.TryGetValue(id, out AuxiliaryTarget? target) ? target :
            throw new ArgumentException("No such auxiliary Vulkan target.", nameof(id));

    private void ResizeAuxiliaryImages(AuxiliaryTarget target, int width, int height)
    {
        int color = 0, depth = 0, framebuffer = 0;
        try
        {
            color = _textures.Create((uint)width, (uint)height, Format.R8G8B8A8Unorm);
            depth = _textures.Create((uint)width, (uint)height, Format.D32Sfloat);
            framebuffer = CreateFramebuffer(width, height);
            _targets.Attach(framebuffer, 0, color);
            _targets.Attach(framebuffer, -1, depth);
        }
        catch
        {
            if (framebuffer != 0) DeleteFramebuffer(framebuffer);
            if (depth != 0) DeleteTexture(depth);
            if (color != 0) DeleteTexture(color);
            throw;
        }
        EndAuxiliaryImageUsers();
        ReleaseAuxiliaryImages(target);
        target.Color = color;
        target.Depth = depth;
        target.Framebuffer = framebuffer;
        target.Width = width;
        target.Height = height;
        // The replacement has not been rendered yet; a request for the old
        // image must not present newly allocated contents instead.
        target.PresentQueued = false;
    }

    private void ReleaseAuxiliaryImages(AuxiliaryTarget target)
    {
        if (target.Framebuffer != 0) DeleteFramebuffer(target.Framebuffer);
        if (target.Depth != 0) DeleteTexture(target.Depth);
        if (target.Color != 0) DeleteTexture(target.Color);
        target.Framebuffer = target.Depth = target.Color = 0;
        target.PresentQueued = false;
    }

    private void EndAuxiliaryImageUsers()
    {
        if (!_frameActive) return;
        EndNativePass();
        // A coalesced draw can have released its native-pass bookkeeping while
        // leaving the surrounding scope open. Close that scope before removing
        // its framebuffer or attachments as well.
        _targets.EndPass(Commands);
        _targets.EndRendering(Commands);
    }

    private void PresentAuxiliaryTargets(ulong renderValue)
    {
        foreach (AuxiliaryTarget target in _auxiliaryTargets.Values)
        {
            if (!target.PresentQueued) continue;
            target.PresentQueued = false;
            (int width, int height) = target.Host.PixelSize;
            if (width <= 0 || height <= 0 || target.Host.IsMinimized) continue;
            // Resize events may arrive after rendering. Present the recorded
            // old-sized image scaled to the current surface; replace the source
            // only when the frontend asks for its next drawable framebuffer.
            if (target.Presenter != null)
            {
                VulkanTexture source = _textures.Get(target.Color) ?? throw new InvalidOperationException("Auxiliary color target is missing.");
                if (target.Presenter.Present(_frames, _textures, source, renderValue, target.Vsync, out ulong isolatedId, out ulong isolatedValue) &&
                    AuxiliaryTracePath is { } isolatedTrace && target.DiagnosticPresents++ < 8)
                    TraceAuxiliary(isolatedTrace, new { @event = "auxiliary-present", frameId = _latencyFrameId,
                        target = target.Framebuffer, texture = target.Color, presentId = isolatedId, accepted = isolatedId != 0,
                        width, height, renderValue, presentValue = isolatedValue, presentation = "DXGI" });
                continue;
            }
            if ((uint)width != target.Swapchain!.Extent.Width || (uint)height != target.Swapchain.Extent.Height)
                target.Swapchain.RequestRebuild((uint)width, (uint)height, target.Vsync);
            if (!target.Swapchain.TryAcquire(out PresentTarget acquired))
            {
                if (target.Swapchain.RebuildFailure is { } failure)
                    AddDiagnostic("auxiliary swapchain recreation failed: " + failure);
                continue;
            }
            CommandBuffer commands = _frames.BeginPresentCommands();
            target.Path!.Record(commands, acquired, _textures.Get(target.Color));
            ulong presentValue = _frames.SubmitPresent(acquired.AcquireSemaphore,
                target.Path.AcquireWaitStage, renderValue, acquired.PresentSemaphore);
            target.Swapchain.NotePresentSubmitted(acquired, presentValue);
            ulong presentId = target.Swapchain.Present(acquired);
            if (AuxiliaryTracePath is { } trace && target.DiagnosticPresents++ < 8)
                TraceAuxiliary(trace, new { @event = "auxiliary-present", frameId = _latencyFrameId,
                    target = target.Framebuffer, texture = target.Color, presentId, accepted = presentId != 0,
                    width, height, renderValue, presentValue });
        }
    }

    private static string? AuxiliaryTracePath
    {
        get
        {
            string? path = Environment.GetEnvironmentVariable("VULKANSTORY_MANAGED_GL_TRACE");
            return !string.IsNullOrWhiteSpace(path) && System.IO.Path.IsPathFullyQualified(path) ? path : null;
        }
    }

    private static void TraceAuxiliary(string path, object record)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.AppendAllText(path, System.Text.Json.JsonSerializer.Serialize(record) + Environment.NewLine);
    }

    private void DisposeAuxiliaryTargets()
    {
        // Device idle has already completed scene work. Each chain also drains
        // presentation before its surface can go; textures remain timeline-owned.
        foreach (AuxiliaryTarget target in _auxiliaryTargets.Values)
        {
            if (target.Presenter != null) target.Presenter.Dispose(); else target.Swapchain!.Dispose();
            ReleaseAuxiliaryImages(target);
        }
        _auxiliaryTargets.Clear();
    }
}
