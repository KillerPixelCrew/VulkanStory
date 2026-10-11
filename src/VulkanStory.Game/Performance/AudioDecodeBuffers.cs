using System.Buffers;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game.Performance;

/// <summary>Leaves the original Vorbis decoder intact; PCM accumulation uses pooled segments instead of repeated growth copies.</summary>
internal static class AudioDecodeBuffers
{
    private sealed class Scope(Scope? previous)
    {
        internal readonly Scope? Previous = previous;
        internal PcmStream? Stream;
    }
    [ThreadStatic] private static Scope? current;
    private static readonly ConstructorInfo Constructor = typeof(MemoryStream).GetConstructor(Type.EmptyTypes)!;
    private static MethodInfo Target() => AccessTools.Method(typeof(OggDecoder), "OggToWav", [typeof(Stream), typeof(IAsset)])!;

    internal static PerformanceFeature Create() => new("audio-decode-buffers", s => s.AudioDecodeBufferReuse, () =>
    {
        if (Target() == null || PatchProcessor.GetOriginalInstructions(Target()).Count(i => i.opcode == OpCodes.Newobj && Equals(i.operand, Constructor)) != 1)
            throw new InvalidOperationException("Vorbis PCM stream construction anchor changed.");
    }, harmony => harmony.Patch(Target(), prefix: new HarmonyMethod(typeof(AudioDecodeBuffers), nameof(Begin)),
        transpiler: new HarmonyMethod(typeof(AudioDecodeBuffers), nameof(Transpiler)),
        finalizer: new HarmonyMethod(typeof(AudioDecodeBuffers), nameof(End))));

    private static void Begin(out Scope? __state)
    {
        __state = PerformancePatchSet.Current?.AudioDecodeBufferReuse == true ? new Scope(current) : null;
        if (__state != null) current = __state;
    }
    private static void End(Scope? __state)
    {
        if (__state == null) return;
        try { __state.Stream?.Dispose(); }
        finally { current = __state.Previous; }
    }
    private static MemoryStream CreateStream()
    {
        if (current == null || PerformancePatchSet.Current?.AudioDecodeBufferReuse != true) return new MemoryStream();
        return current.Stream = new PcmStream();
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Newobj && Equals(instruction.operand, Constructor))
            { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(AudioDecodeBuffers), nameof(CreateStream)); }
            yield return instruction;
        }
    }

    private sealed class PcmStream : MemoryStream
    {
        private const int SegmentSize = 64 * 1024;
        private readonly List<byte[]> segments = new();
        private int length;
        private bool disposed;
        public override void Write(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (buffer.Length - offset < count) throw new ArgumentException("Invalid PCM source range.");
            _ = checked(length + count);
            while (count != 0)
            {
                int segment = length / SegmentSize, position = length % SegmentSize;
                if (segment == segments.Count) segments.Add(ArrayPool<byte>.Shared.Rent(SegmentSize));
                int copy = Math.Min(count, SegmentSize - position);
                buffer.AsSpan(offset, copy).CopyTo(segments[segment].AsSpan(position, copy));
                offset += copy; count -= copy; length += copy;
            }
        }
        public override byte[] ToArray()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            byte[] result = new byte[length];
            int offset = 0;
            foreach (byte[] segment in segments)
            {
                int copy = Math.Min(SegmentSize, length - offset);
                segment.AsSpan(0, copy).CopyTo(result.AsSpan(offset, copy)); offset += copy;
            }
            return result; // AudioMetaData owns this exact-length array, never a rented buffer.
        }
        protected override void Dispose(bool disposing)
        {
            if (!disposed)
            {
                disposed = true;
                foreach (byte[] segment in segments) ArrayPool<byte>.Shared.Return(segment);
                segments.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
