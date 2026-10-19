using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Vulkan;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Render.Vulkan.Culling;

/// <summary>
/// PERF-03 occlusion depth: after the caller's occluders are drawn, one compute pass reduces Primary's depth to the farthest
/// value of every <see cref="Block" /> x <see cref="Block" /> tile, and the small result is copied into the frame slot's readback
/// arena. When that slot comes round again its frame has completed, so the bytes are read without waiting, the remaining
/// levels are reduced on the CPU, and the result is published as an immutable <see cref="OcclusionPyramid" />.
/// </summary>
/// <remarks>The published pyramid is therefore <c>FramesInFlight</c> frames old; the caller compensates for camera movement.</remarks>
internal sealed class OcclusionDepth : IDisposable
{
    public const int Block = 8;
    private const string ResourcePrefix = "shaders-vk/occlusion/";

    private sealed record Pending(int Width, int Height, OcclusionCamera Camera, OcclusionProjection Projection);

    private readonly VulkanDevice _device;
    private readonly Pending?[] _pending;
    // One mapped buffer per frame slot, owned here: the shared readback arena can be replaced (and retired) within a frame.
    private readonly VulkanBuffer?[] _readbacks;
    private readonly byte[] _push = new byte[16];
    private int _program, _target;
    private Format _programFormat;
    private int _width, _height;
    private int _mipProgram;
    internal int GpuTexture => GpuCamera == null ? 0 : _target;
    internal OcclusionCamera? GpuCamera { get; private set; }
    internal int GpuWidth { get; private set; }
    internal int GpuHeight { get; private set; }
    internal int SourceDepthTexture { get; private set; }
    private OcclusionPyramid? _latest;
    private OcclusionPyramid? _spare;
    private readonly object _publicationGate = new();

    /// <summary>Reuses size-matched CPU hierarchy storage. Applies to the next completed readback.</summary>
    public bool ReuseStorage { get; set; } = true;

    public OcclusionDepth(VulkanDevice device, int framesInFlight)
    {
        _device = device;
        _pending = new Pending?[framesInFlight];
        _readbacks = new VulkanBuffer?[framesInFlight];
        Test = new LatestView(this);
    }

    /// <summary>The newest published pyramid; queries retain a complete snapshot until they finish.</summary>
    public IOcclusionTest Test { get; }

    /// <summary>Why the last <see cref="Capture" /> recorded nothing, or null.</summary>
    public string? LastError { get; private set; }

    /// <summary>Pyramids published since creation (diagnostics).</summary>
    public long Published { get; private set; }

    /// <summary>
    /// Records the reduction of <paramref name="depthTexture" /> and its copy to the host into the open frame. The caller has
    /// finished drawing the occluders; the device closes any open rendering scope for the compute pass.
    /// </summary>
    public bool Capture(int depthTexture, OcclusionCamera camera, bool prepareGpuDraws = false)
    {
        LastError = null;
        GpuCamera = null;
        if (OcclusionProjection.From(camera.Projection) is not { } projection) return Fail("the projection is not a GL perspective matrix");
        if (camera.View is not { Length: >= 16 }) return Fail("the view matrix is missing");
        VulkanTexture? depth = _device.TextureOf(depthTexture);
        if (depth == null) return Fail("depth texture missing");
        int width = (int)((depth.Width + Block - 1) / Block), height = (int)((depth.Height + Block - 1) / Block);
        if (!EnsureTarget(width, height)) return Fail("occlusion target could not be created");
        if (!EnsureProgram()) return false;

        BitConverter.TryWriteBytes(_push.AsSpan(0), (int)depth.Width);
        BitConverter.TryWriteBytes(_push.AsSpan(4), (int)depth.Height);
        BitConverter.TryWriteBytes(_push.AsSpan(8), Block);
        if (!_device.RecordComputePass(new ComputePassDeclaration
        {
            Name = "occlusion-reduce",
            ProgramId = _program,
            Bindings = new[]
            {
                new ComputeBinding(0, depthTexture, ComputeAccess.Sampled),
                new ComputeBinding(1, _target, ComputeAccess.StorageWrite),
            },
            Dispatches = new[] { ComputeDispatch.Covering(1, (byte[])_push.Clone()) },
        })) return Fail(_device.GetError());

        if (prepareGpuDraws)
        {
            if (_mipProgram == 0)
                _mipProgram = _device.CreateComputeProgram(Source("pyramid.comp"), "occlusion-pyramid",
                    [new ComputeSlot(0, ComputeSlotKind.Sampled), new ComputeSlot(1, ComputeSlotKind.Storage)], 0);
            if (_mipProgram == 0) return Fail(_device.GetError());
            uint levels = _device.TextureOf(_target)!.MipLevels;
            for (uint level = 1; level < levels; level++)
                if (!_device.RecordComputePass(new ComputePassDeclaration
                {
                    Name = "occlusion-pyramid", ProgramId = _mipProgram,
                    Bindings = [new ComputeBinding(0, _target, ComputeAccess.Sampled, level - 1),
                        new ComputeBinding(1, _target, ComputeAccess.StorageWrite, level)],
                    Dispatches = [ComputeDispatch.Covering(1)],
                })) return Fail(_device.GetError());
            GpuCamera = camera;
            SourceDepthTexture = depthTexture;
            GpuWidth = (int)depth.Width; GpuHeight = (int)depth.Height;
        }

        int slot = _device.CurrentFrameSlot;
        ulong bytes = (ulong)width * (ulong)height * sizeof(float);
        if (_readbacks[slot] is not { } buffer || buffer.Size < bytes)
        {
            // The slot's previous frame has completed (its bytes were collected), but retire through the timeline regardless.
            if (_readbacks[slot] is { } old) _device.DeferBufferDeletion(old);
            buffer = _readbacks[slot] = _device.CreateReadbackBuffer(bytes);
        }
        _device.CopyTextureToReadbackBuffer(_target, (uint)width, (uint)height, buffer);
        _pending[slot] = new Pending(width, height, camera, projection);
        return true;
    }

    /// <summary>
    /// Called when <paramref name="slotIndex" /> begins a frame, after its previous frame completed and before its readback arena
    /// is reused: publishes the pyramid that frame captured.
    /// </summary>
    public void Collect(int slotIndex)
    {
        if (_pending[slotIndex] is not { } pending || _readbacks[slotIndex] is not { } buffer) return;
        _pending[slotIndex] = null;
        // Only LatestView exposes the published pyramid, and its queries hold the publication gate.
        // The spare is unreachable by readers; build it outside the gate, then retire the old snapshot
        // only after all queries using it have finished. Neither readers nor the GPU retain spare storage.
        OcclusionPyramid next = OcclusionPyramid.FromReadback(buffer.Mapped, pending.Width, pending.Height,
            pending.Camera, pending.Projection, ReuseStorage ? _spare : null);
        lock (_publicationGate)
        {
            _spare = ReuseStorage ? _latest : null;
            _latest = next;
        }
        Published++;
    }

    /// <summary>Drops pending captures and the published pyramid (a world change or a target rebuild).</summary>
    public void Reset()
    {
        GpuCamera = null;
        Array.Clear(_pending);
        lock (_publicationGate) { _latest = null; _spare = null; }
    }

    private bool Fail(string reason)
    {
        LastError = string.IsNullOrEmpty(reason) ? "occlusion capture refused" : reason;
        return false;
    }

    private bool EnsureTarget(int width, int height)
    {
        if (_target != 0 && _width == width && _height == height) return true;
        if (_target != 0) _device.DeleteTexture(_target);
        // Captures still in flight name the old target's bytes in the arena, not the texture, so they stay valid.
        int levels = 1;
        for (int extent = Math.Max(width, height); extent > 1; extent >>= 1) levels++;
        _target = _device.CreateStorageTexture(width, height, Format.R32Sfloat, levels);
        _width = width; _height = height;
        return _target != 0 && _device.TextureOf(_target)?.Format == Format.R32Sfloat;
    }

    private bool EnsureProgram()
    {
        Format format = _device.TextureOf(_target)!.Format;
        if (_program != 0 && format == _programFormat) return true;
        if (_program != 0) _device.DeleteComputeProgram(_program);
        string source = Source("reduce.comp");
        int versionEnd = source.IndexOf('\n', source.IndexOf("#version", StringComparison.Ordinal)) + 1;
        source = source.Insert(versionEnd, "#define OCCLUSION_FORMAT r32f\n");
        _program = _device.CreateComputeProgram(source, "occlusion-reduce",
            [new ComputeSlot(0, ComputeSlotKind.Sampled), new ComputeSlot(1, ComputeSlotKind.Storage)], (uint)_push.Length);
        _programFormat = format;
        return _program != 0 || Fail("occlusion reduce shader failed to compile: " + _device.GetError());
    }

    internal static string Source(string fileName)
    {
        using Stream? stream = typeof(OcclusionDepth).Assembly.GetManifestResourceStream(ResourcePrefix + fileName);
        if (stream == null) throw new FileNotFoundException("embedded occlusion shader missing: " + ResourcePrefix + fileName);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Frees the readback buffers directly; only once the device is idle (teardown).</summary>
    public void DisposeIdle()
    {
        Reset();
        for (int i = 0; i < _readbacks.Length; i++) { _readbacks[i]?.Dispose(); _readbacks[i] = null; }
    }

    public void Dispose()
    {
        Reset();
        for (int i = 0; i < _readbacks.Length; i++) { if (_readbacks[i] is { } buffer) _device.DeferBufferDeletion(buffer); _readbacks[i] = null; }
        if (_target != 0) _device.DeleteTexture(_target);
        if (_program != 0) _device.DeleteComputeProgram(_program);
        if (_mipProgram != 0) _device.DeleteComputeProgram(_mipProgram);
        _target = _program = _mipProgram = 0;
    }

    /// <summary>Forwards to the newest published pyramid; undecided (visible) while none exists.</summary>
    private sealed class LatestView(OcclusionDepth owner) : IOcclusionTest
    {
        public OcclusionCamera? Source { get { lock (owner._publicationGate) return owner._latest?.Camera; } }
        public bool IsOccluded(double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
        {
            lock (owner._publicationGate)
                return owner._latest?.IsOccluded(minX, minY, minZ, maxX, maxY, maxZ) == true;
        }
    }
}

/// <summary>Depth linearisation of a column-major GL perspective matrix (<c>m[10] = A</c>, <c>m[14] = B</c>, <c>m[11] = -1</c>).</summary>
internal readonly record struct OcclusionProjection(float DepthUnpackMul, float DepthUnpackAdd)
{
    public static OcclusionProjection? From(float[]? m)
    {
        if (m == null || m.Length < 16) return null;
        if (MathF.Abs(m[11] + 1f) > 1e-4f || MathF.Abs(m[15]) > 1e-4f || m[14] == 0f) return null;
        return new OcclusionProjection(-m[14] / 2f, (1f - m[10]) / 2f);
    }

    /// <summary>Forward view distance of a [0, 1] depth value (the device's GL z remap); infinite at the far plane and beyond.</summary>
    public float ViewDepth(float depth)
    {
        float denominator = DepthUnpackAdd - depth;
        return denominator <= 0f ? float.PositiveInfinity : DepthUnpackMul / denominator;
    }
}

/// <summary>
/// One published occlusion depth: a max-reduced pyramid of forward view distances over the captured view, with the camera
/// that drew it. Immutable while published; OcclusionDepth recycles it only after all readers finish.
/// </summary>
internal sealed class OcclusionPyramid
{
    // An occluder must be at least this much nearer (relative and absolute, in blocks) than the box to hide it.
    private const float RelativeMargin = 0.002f;
    private const float AbsoluteMargin = 0.25f;

    private readonly float[][] _levels;
    private readonly int[] _widths, _heights;
    private float[] _view, _projection;

    public OcclusionCamera Camera { get; private set; }

    private OcclusionPyramid(float[][] levels, int[] widths, int[] heights, OcclusionCamera camera)
    {
        _levels = levels; _widths = widths; _heights = heights; Camera = camera;
        _view = camera.View; _projection = camera.Projection;
    }

    /// <summary>Linearises the base level and reduces it to 1x1, every level keeping the farthest distance of its children.</summary>
    public static OcclusionPyramid Build(float[] depth, int width, int height, OcclusionCamera camera, OcclusionProjection projection)
    {
        OcclusionPyramid result = CreateStorage(width, height, camera);
        depth.AsSpan().CopyTo(result._levels[0]);
        result.Reduce(camera, projection);
        return result;
    }

    internal static OcclusionPyramid FromReadback(IntPtr mapped, int width, int height, OcclusionCamera camera,
        OcclusionProjection projection, OcclusionPyramid? reuse)
    {
        OcclusionPyramid result = reuse != null && reuse._widths[0] == width && reuse._heights[0] == height
            ? reuse : CreateStorage(width, height, camera);
        Marshal.Copy(mapped, result._levels[0], 0, width * height);
        result.Reduce(camera, projection);
        return result;
    }

    private static OcclusionPyramid CreateStorage(int width, int height, OcclusionCamera camera)
    {
        var levels = new System.Collections.Generic.List<float[]>();
        var widths = new System.Collections.Generic.List<int>();
        var heights = new System.Collections.Generic.List<int>();
        levels.Add(new float[width * height]); widths.Add(width); heights.Add(height);
        while (width > 1 || height > 1)
        {
            width = (width + 1) / 2; height = (height + 1) / 2;
            levels.Add(new float[width * height]); widths.Add(width); heights.Add(height);
        }
        return new OcclusionPyramid(levels.ToArray(), widths.ToArray(), heights.ToArray(), camera);
    }

    private void Reduce(OcclusionCamera camera, OcclusionProjection projection)
    {
        Camera = camera; _view = camera.View; _projection = camera.Projection;
        float[] level = _levels[0];
        for (int i = 0; i < level.Length; i++) level[i] = projection.ViewDepth(level[i]);
        for (int index = 1; index < _levels.Length; index++)
        {
            int width = _widths[index - 1], height = _heights[index - 1];
            int nextWidth = _widths[index], nextHeight = _heights[index];
            float[] next = _levels[index];
            for (int y = 0; y < nextHeight; y++)
                for (int x = 0; x < nextWidth; x++)
                {
                    int sx = x * 2, sy = y * 2, sx1 = Math.Min(sx + 1, width - 1), sy1 = Math.Min(sy + 1, height - 1);
                    next[y * nextWidth + x] = MathF.Max(MathF.Max(level[sy * width + sx], level[sy * width + sx1]),
                        MathF.Max(level[sy1 * width + sx], level[sy1 * width + sx1]));
                }
            level = next;
        }
    }

    /// <summary>
    /// True only when the box lies wholly inside the captured view, in front of the camera, and farther than the farthest occluder
    /// over its whole screen rectangle. Anything the captured view cannot decide counts as visible.
    /// </summary>
    public bool IsOccluded(double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
    {
        float[] v = _view, p = _projection;
        float nearest = float.PositiveInfinity;
        float left = float.PositiveInfinity, right = float.NegativeInfinity, bottom = float.PositiveInfinity, top = float.NegativeInfinity;
        for (int corner = 0; corner < 8; corner++)
        {
            float x = (float)(((corner & 1) == 0 ? minX : maxX) - Camera.CameraX);
            float y = (float)(((corner & 2) == 0 ? minY : maxY) - Camera.CameraY);
            float z = (float)(((corner & 4) == 0 ? minZ : maxZ) - Camera.CameraZ);
            // View space (column-major): GL looks down -z.
            float vx = v[0] * x + v[4] * y + v[8] * z + v[12];
            float vy = v[1] * x + v[5] * y + v[9] * z + v[13];
            float vz = v[2] * x + v[6] * y + v[10] * z + v[14];
            float vw = v[3] * x + v[7] * y + v[11] * z + v[15];
            float cx = p[0] * vx + p[4] * vy + p[8] * vz + p[12] * vw;
            float cy = p[1] * vx + p[5] * vy + p[9] * vz + p[13] * vw;
            float cw = p[3] * vx + p[7] * vy + p[11] * vz + p[15] * vw;
            // A corner at or behind the camera plane: the box surrounds or touches the camera; never occluded.
            if (cw <= 1e-3f) return false;
            float forward = -vz / (vw == 0f ? 1f : vw);
            nearest = MathF.Min(nearest, forward);
            float nx = cx / cw, ny = cy / cw;
            left = MathF.Min(left, nx); right = MathF.Max(right, nx);
            bottom = MathF.Min(bottom, ny); top = MathF.Max(top, ny);
        }
        // Partly or wholly outside the captured view: what lies outside was never drawn there.
        if (left < -1f || right > 1f || bottom < -1f || top > 1f) return false;

        int width = _widths[0], height = _heights[0];
        int x0 = Math.Clamp((int)((left * 0.5f + 0.5f) * width), 0, width - 1);
        int x1 = Math.Clamp((int)((right * 0.5f + 0.5f) * width), 0, width - 1);
        // Rows run bottom-up: row 0 is NDC y = -1.
        int y0 = Math.Clamp((int)((bottom * 0.5f + 0.5f) * height), 0, height - 1);
        int y1 = Math.Clamp((int)((top * 0.5f + 0.5f) * height), 0, height - 1);
        // One texel of slack on every side covers sub-texel jitter and rounding at the base level.
        x0 = Math.Max(0, x0 - 1); y0 = Math.Max(0, y0 - 1);
        x1 = Math.Min(width - 1, x1 + 1); y1 = Math.Min(height - 1, y1 + 1);
        int level = 0;
        // Coarsen until the rectangle spans at most 4 x 4 texels; a parent covers its children's farthest value.
        while (level < _levels.Length - 1 && (x1 - x0 > 3 || y1 - y0 > 3))
        {
            x0 >>= 1; x1 >>= 1; y0 >>= 1; y1 >>= 1; level++;
        }
        float[] texels = _levels[level];
        int stride = _widths[level];
        float farthest = 0f;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                farthest = MathF.Max(farthest, texels[y * stride + x]);
        return nearest > farthest * (1f + RelativeMargin) + AbsoluteMargin;
    }
}
