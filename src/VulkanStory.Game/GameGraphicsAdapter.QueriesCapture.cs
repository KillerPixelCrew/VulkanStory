using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.ClientNative;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    private static readonly AccessTools.FieldRef<ClientPlatformWindows, Screenshot?> ScreenshotField =
        AccessTools.FieldRefAccess<ClientPlatformWindows, Screenshot?>("screenshot");
    private static readonly ConditionalWeakTable<Screenshot, GameGraphicsAdapter> ScreenshotOwners = new();
    private static readonly ConditionalWeakTable<IAviWriter, GameGraphicsAdapter> VideoOwners = new();
    private readonly List<WeakReference<IAviWriter>> videoWriters = new();
    private readonly HashSet<int> queries = new();
    private readonly HashSet<int> releasedQueries = new();

    internal int CreateOcclusionQuery()
    {
        int query = RequireDevice().CreateOcclusionQuery();
        releasedQueries.Remove(query);
        queries.Add(query); return query;
    }
    private void RequireQuery(int query)
    {
        RequireDevice();
        if (!queries.Contains(query)) throw new InvalidOperationException("Occlusion query has no renderer owner.");
    }
    internal void BeginOcclusionQuery(int query) { RequireQuery(query); RequireDevice().BeginOcclusionQuery(query); }
    internal void EndOcclusionQuery(int query) { RequireQuery(query); RequireDevice().EndOcclusionQuery(query); }
    internal int ReadOcclusionQuery(int query, bool availability)
    {
        RequireQuery(query);
        return availability ? (RequireDevice().IsQueryResultAvailable(query) ? 1 : 0) : RequireDevice().GetQueryResult(query);
    }
    internal void DeleteOcclusionQuery(int query)
    {
        RequireDevice();
        if (releasedQueries.Contains(query)) return;
        RequireQuery(query);
        RequireDevice().DeleteQuery(query);
        queries.Remove(query);
        releasedQueries.Add(query);
    }
    private void DetachCaptureService()
    {
        Screenshot? capture = platform is null ? null : ScreenshotField(platform);
        if (capture != null && ScreenshotOwners.TryGetValue(capture, out var owner) && ReferenceEquals(owner, this))
            ScreenshotOwners.Remove(capture);
        foreach (var reference in videoWriters)
            if (reference.TryGetTarget(out var writer) && VideoOwners.TryGetValue(writer, out var videoOwner) &&
                ReferenceEquals(videoOwner, this)) VideoOwners.Remove(writer);
        videoWriters.Clear();
    }
    internal void AssociateVideoWriter(IAviWriter writer)
    {
        RequireDevice();
        ArgumentNullException.ThrowIfNull(writer);
        if (VideoOwners.TryGetValue(writer, out var owner))
        {
            if (!ReferenceEquals(owner, this)) throw new InvalidOperationException("Video writer belongs to another renderer.");
            return;
        }
        VideoOwners.Add(writer, this);
        videoWriters.RemoveAll(reference => !reference.TryGetTarget(out _));
        videoWriters.Add(new(writer));
    }
    internal static GameGraphicsAdapter VideoOwner(IAviWriter writer) => VideoOwners.TryGetValue(writer, out var owner)
        ? owner : throw new InvalidOperationException("Active video writer has no Vulkan capture owner.");
    internal Screenshot CaptureService()
    {
        RequireDevice();
        Screenshot capture = ScreenshotField(platform!) ?? throw new InvalidOperationException("Original screenshot service is not initialized.");
        if (ScreenshotOwners.TryGetValue(capture, out var existing))
        {
            if (!ReferenceEquals(existing, this)) throw new InvalidOperationException("Screenshot service belongs to another renderer.");
        }
        else ScreenshotOwners.Add(capture, this);
        return capture;
    }
    internal static GameGraphicsAdapter CaptureOwner(Screenshot capture) => ScreenshotOwners.TryGetValue(capture, out var adapter)
        ? adapter : throw new InvalidOperationException("Screenshot service has no renderer owner.");
    internal Size2i CaptureSize()
    {
        CurrentFramebufferHandle();
        if (currentFramebuffer != null) return new Size2i(currentFramebuffer.Width, currentFramebuffer.Height);
        var pixels = RequireFramebufferHost().PixelSize();
        return new Size2i(pixels.Width, pixels.Height);
    }
    internal (int Width, int Height) CaptureDisplaySize() { RequireDevice(); return RequireFramebufferHost().PixelSize(); }
    internal string SaveScreenshot(string? path, string? filename, bool alpha, bool flip, string? metadata) =>
        CaptureService().SaveScreenshot(platform!, CaptureSize(), path, filename, alpha, flip, metadata);
    internal BitmapRef GrabScreenshot(bool alpha, bool scale) =>
        new BitmapExternal(CaptureService().GrabScreenshot(CaptureSize(), scale, false, alpha));
    internal BitmapRef GrabScreenshot(int width, int height, bool scale, bool flip, bool alpha) =>
        new BitmapExternal(CaptureService().GrabScreenshot(new Size2i(width, height), scale, flip, alpha));
    internal void ReadCapturePixels(int x, int y, int width, int height, IntPtr destination) =>
        RequireDevice().ReadFramebufferBgra8(CurrentFramebufferHandle(), x, y, width, height, destination);
}
