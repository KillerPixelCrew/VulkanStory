using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Security.Cryptography;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>
/// Decides when the driver's pipeline cache has grown enough to be worth writing out
/// before shutdown.
///
/// A session that crashes, or is killed, loses everything a shutdown-only save would
/// have written. Godot saves from a worker when the cache has grown by some megabytes
/// rather than on a timer (docs/vulkan.md#caches §1, godot#76348); this is that
/// rule, with the size sampled at most once per interval because the size query goes
/// through the driver.
/// </summary>
internal sealed class PipelineCacheGrowthTrigger
{
    public const long DefaultThresholdBytes = 8L * 1024 * 1024;

    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

    private readonly long _thresholdBytes;
    private readonly long _intervalTicks;
    private long _lastSampleTimestamp;
    private long _baselineBytes;

    /// <param name="thresholdBytes">Required cache growth above the saved baseline before writing; clamped to at least one byte.</param>
    /// <param name="interval">Interval between cache-size samples, converted to the monotonic Stopwatch clock.</param>
    /// <param name="baselineBytes">What is on disk already: the seed the cache was created from, or 0.</param>
    public PipelineCacheGrowthTrigger(long thresholdBytes, TimeSpan interval, long baselineBytes)
    {
        _thresholdBytes = Math.Max(1, thresholdBytes);
        _intervalTicks = (long)(interval.TotalSeconds * Stopwatch.Frequency);
        _baselineBytes = baselineBytes;
    }

    /// <summary>Byte size recorded at the most recent completed cache save.</summary>
    public long BaselineBytes => Interlocked.Read(ref _baselineBytes);

    /// <summary>
    /// Whether a size sample is due at <paramref name="timestamp" /> (Stopwatch ticks). The
    /// first call only arms the clock; afterwards at most one sample per interval.
    /// </summary>
    public bool SampleDue(long timestamp)
    {
        if (_lastSampleTimestamp == 0)
        {
            _lastSampleTimestamp = timestamp;
            return false;
        }
        if (timestamp - _lastSampleTimestamp < _intervalTicks) return false;
        _lastSampleTimestamp = timestamp;
        return true;
    }

    /// <summary>The cache is at least the threshold larger than what was last written.</summary>
    public bool GrewEnough(long currentBytes) => currentBytes - BaselineBytes >= _thresholdBytes;

    /// <summary>Updates the interlocked byte-size baseline after a successful save.</summary>
    public void NoteSaved(long bytes) => Interlocked.Exchange(ref _baselineBytes, bytes);
}

/// <summary>
/// The pipeline cache and pipeline-key log files of one GPU, and the saves that write them.
///
/// The render thread only calls <see cref="Tick" />, a timestamp comparison; the size query,
/// the serialisation and the file writes run on a thread-pool task, one at a time. The
/// shutdown save stays: <see cref="SaveAtShutdown" /> waits for a running save and writes
/// both files once more.
/// </summary>
internal sealed class PipelineCachePersistence
{
    private readonly PipelineCacheIdentity _identity;
    private readonly PipelineCacheGrowthTrigger _trigger;
    private Task? _pending;
    private long _saves;

    /// <summary>Device/driver-specific on-disk pipeline-cache path.</summary>
    public string CachePath { get; }
    /// <summary>On-disk log of pipeline requests used for prewarming.</summary>
    public string KeyLogPath { get; }
    /// <summary>Owned pipeline-request log associated with this persistence configuration.</summary>
    public PipelineKeyLog KeyLog { get; }

    /// <summary>Where a failed write is reported (the validation mirror in the device).</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Driver cache files written, opportunistic and shutdown saves together.</summary>
    public long Saves => Interlocked.Read(ref _saves);

    private PipelineCachePersistence(string cachePath, string keyLogPath, PipelineCacheIdentity identity,
        PipelineKeyLog keyLog, PipelineCacheGrowthTrigger trigger)
    {
        CachePath = cachePath;
        KeyLogPath = keyLogPath;
        _identity = identity;
        KeyLog = keyLog;
        _trigger = trigger;
    }

    /// <summary>Loads both files for <paramref name="identity" />; <paramref name="seed" /> is the usable driver blob or null.</summary>
    public static PipelineCachePersistence Open(string cacheRoot, PipelineCacheIdentity identity, out byte[]? seed,
        long thresholdBytes = PipelineCacheGrowthTrigger.DefaultThresholdBytes, TimeSpan? interval = null)
    {
        string cachePath = PipelineCacheFile.PathFor(cacheRoot, identity);
        string keyLogPath = PipelineKeyLog.PathFor(cacheRoot, identity);
        seed = PipelineCacheFile.Load(cachePath, identity);
        return new PipelineCachePersistence(cachePath, keyLogPath, identity, PipelineKeyLog.Load(keyLogPath),
            new PipelineCacheGrowthTrigger(thresholdBytes, interval ?? PipelineCacheGrowthTrigger.DefaultInterval,
                seed?.Length ?? 0));
    }

    /// <summary>
    /// Render thread, once per frame: starts a background save pass when a sample is due
    /// and no pass is running. True when one started.
    /// </summary>
    public bool Tick(GraphicsPipelineCache cache, long timestamp)
    {
        if (_pending is { IsCompleted: false }) return false;
        if (!_trigger.SampleDue(timestamp)) return false;
        _pending = Task.Run(() => BackgroundPass(cache));
        return true;
    }

    /// <summary>Waits for a running background pass. Before the cache is disposed.</summary>
    public void WaitForPendingSave()
    {
        Task? pending = _pending;
        if (pending == null) return;
        try
        {
            pending.Wait();
        }
        catch (AggregateException error)
        {
            Log?.Invoke("--- pipeline cache background save failed: " + error.InnerException?.Message);
        }
    }

    /// <summary>Attempts final pipeline-driver cache and key-log persistence during renderer shutdown.</summary>
    public void SaveAtShutdown(GraphicsPipelineCache cache)
    {
        WaitForPendingSave();
        SaveDriverCache(cache);
        if (KeyLog.HasUnsavedChanges && !KeyLog.Save(KeyLogPath))
        {
            Log?.Invoke("--- pipeline key log not saved to " + KeyLogPath);
        }
    }

    private void BackgroundPass(GraphicsPipelineCache cache)
    {
        long size = cache.DriverCacheSize();
        VulkanStats.NotePipelineCacheBytes(size);
        if (_trigger.GrewEnough(size)) SaveDriverCache(cache);
        if (KeyLog.HasUnsavedChanges && !KeyLog.Save(KeyLogPath))
        {
            Log?.Invoke("--- pipeline key log not saved to " + KeyLogPath);
        }
    }

    private void SaveDriverCache(GraphicsPipelineCache cache)
    {
        byte[] blob = cache.SerializeDriverCache();
        if (blob.Length == 0) return;
        VulkanStats.NotePipelineCacheBytes(blob.Length);
        if (!PipelineCacheFile.Save(CachePath, blob, _identity))
        {
            Log?.Invoke("--- pipeline cache not saved to " + CachePath);
            return;
        }
        _trigger.NoteSaved(blob.Length);
        Interlocked.Increment(ref _saves);
        VulkanStats.NotePipelineCacheSave();
    }
}

/// <summary>The device and driver a pipeline cache blob was produced by.</summary>
internal readonly record struct PipelineCacheIdentity(uint VendorId, uint DeviceId, uint DriverVersion, byte[] Uuid)
{
    /// <summary>Builds cache compatibility identity from device vendor, device ID, driver version and pipeline-cache UUID.</summary>
    public static PipelineCacheIdentity Of(VulkanCapabilities capabilities) => new(
        capabilities.VendorId, capabilities.DeviceId, capabilities.DriverVersion, capabilities.PipelineCacheUuid);

    /// <summary>One file per GPU, so switching between two GPUs keeps both caches warm.</summary>
    public string FileName => $"{VendorId:x4}-{DeviceId:x4}-{Convert.ToHexStringLower(Uuid)}.bin";
}

/// <summary>
/// The driver's pipeline cache blob on disk, wrapped so that it is only ever handed
/// back to the driver that wrote it, whole.
///
/// The spec says a driver must start empty when the blob's own header does not match
/// it, but drivers have been seen to skip that check and crash after a driver update,
/// to keep the UUID across incompatible builds, and to fail on a zero-length blob;
/// files have been seen truncated, zero-filled and empty. So the wrapper records
/// the vendor, device, driver version, pointer size and UUID alongside a SHA-256 of
/// the blob. Every field and the blob's own Vulkan header are checked before loading,
/// and anything that fails is treated as no cache at all.
/// Design and sources: docs/vulkan.md#caches §1 and "Design for this renderer" item 2.
/// </summary>
internal static class PipelineCacheFile
{
    /// <summary>"OPLC", little-endian.</summary>
    internal const uint FileMagic = 0x434C504F;

    internal const uint FormatVersion = 1;

    /// <summary>Magic, version, data size, SHA-256, vendor, device, driver version, pointer size, UUID.</summary>
    internal const int HeaderSize = 4 + 4 + 8 + 32 + 4 + 4 + 4 + 4 + 16;

    /// <summary>VkPipelineCacheHeaderVersionOne: size, version, vendor, device, UUID.</summary>
    private const int VulkanHeaderSize = 32;

    private const uint VulkanHeaderVersionOne = 1;

    /// <summary>Builds the per-device/driver cache path under the selected cache root.</summary>
    public static string PathFor(string cacheRoot, PipelineCacheIdentity identity) =>
        Path.Combine(cacheRoot, "pipeline", identity.FileName);

    /// <summary>The blob stored for <paramref name="identity" />, or null when there is none it can use.</summary>
    public static byte[]? Load(string path, PipelineCacheIdentity identity)
    {
        byte[]? file = CacheFileWriter.TryReadAll(path);
        return file == null ? null : Unwrap(file, identity);
    }

    /// <summary>Stores a blob; false when it was empty, not a pipeline cache, or could not be written.</summary>
    public static bool Save(string path, byte[] data, PipelineCacheIdentity identity)
    {
        if (!HasMatchingVulkanHeader(data, identity)) return false;
        return CacheFileWriter.WriteAtomically(path, Wrap(data, identity));
    }

    /// <summary>Serializes driver-cache bytes with the renderer cache identity/header.</summary>
    internal static byte[] Wrap(byte[] data, PipelineCacheIdentity identity)
    {
        var file = new byte[HeaderSize + data.Length];
        Span<byte> header = file.AsSpan(0, HeaderSize);
        BitConverter.TryWriteBytes(header[0..], FileMagic);
        BitConverter.TryWriteBytes(header[4..], FormatVersion);
        BitConverter.TryWriteBytes(header[8..], (ulong)data.Length);
        SHA256.HashData(data, header.Slice(16, 32));
        BitConverter.TryWriteBytes(header[48..], identity.VendorId);
        BitConverter.TryWriteBytes(header[52..], identity.DeviceId);
        BitConverter.TryWriteBytes(header[56..], identity.DriverVersion);
        BitConverter.TryWriteBytes(header[60..], (uint)IntPtr.Size);
        identity.Uuid.AsSpan(0, 16).CopyTo(header[64..]);
        data.CopyTo(file, HeaderSize);
        return file;
    }

    /// <summary>The blob inside a file written for exactly <paramref name="identity" />, or null.</summary>
    internal static byte[]? Unwrap(byte[] file, PipelineCacheIdentity identity)
    {
        if (file.Length <= HeaderSize) return null;
        ReadOnlySpan<byte> header = file.AsSpan(0, HeaderSize);
        if (BitConverter.ToUInt32(header[0..]) != FileMagic) return null;
        if (BitConverter.ToUInt32(header[4..]) != FormatVersion) return null;
        if (BitConverter.ToUInt64(header[8..]) != (ulong)(file.Length - HeaderSize)) return null;
        if (BitConverter.ToUInt32(header[48..]) != identity.VendorId) return null;
        if (BitConverter.ToUInt32(header[52..]) != identity.DeviceId) return null;
        if (BitConverter.ToUInt32(header[56..]) != identity.DriverVersion) return null;
        if (BitConverter.ToUInt32(header[60..]) != (uint)IntPtr.Size) return null;
        if (!header.Slice(64, 16).SequenceEqual(identity.Uuid)) return null;

        ReadOnlySpan<byte> data = file.AsSpan(HeaderSize);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);
        if (!hash.SequenceEqual(header.Slice(16, 32))) return null;

        byte[] blob = data.ToArray();
        return HasMatchingVulkanHeader(blob, identity) ? blob : null;
    }

    /// <summary>The blob's own VkPipelineCacheHeaderVersionOne names this device.</summary>
    internal static bool HasMatchingVulkanHeader(byte[] data, PipelineCacheIdentity identity)
    {
        if (data.Length < VulkanHeaderSize || identity.Uuid is not { Length: 16 }) return false;
        ReadOnlySpan<byte> header = data;
        return BitConverter.ToUInt32(header[0..]) >= VulkanHeaderSize
            && BitConverter.ToUInt32(header[4..]) == VulkanHeaderVersionOne
            && BitConverter.ToUInt32(header[8..]) == identity.VendorId
            && BitConverter.ToUInt32(header[12..]) == identity.DeviceId
            && header.Slice(16, 16).SequenceEqual(identity.Uuid);
    }
}
