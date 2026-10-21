using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VulkanStory.Contracts;

namespace VulkanStory.Game.Performance;

/// <summary>Captures visual evolution from stock pool producers without moving physics or provider callbacks.</summary>
internal static class GpuParticleVisuals
{
    internal sealed class Batch
    {
        internal ParticleEvolution[] Properties = [];
        internal int Count, Deferred;
        internal bool Pending;
        internal void Slot(int index, int capacity)
        {
            if (index == 0) { Count = Deferred = 0; Pending = true; }
            if (Properties.Length < (index + 1) * 5)
                Array.Resize(ref Properties, Math.Max((index + 1) * 5, Math.Min(capacity * 5, Math.Max(160, Properties.Length * 2))));
            Count = Math.Max(Count, index + 1);
        }
        internal ReadOnlySpan<ParticleEvolution> Values => Properties.AsSpan(0, Count * 5);
    }
    private static readonly ConditionalWeakTable<MeshData, Batch> batches = new();
    private static readonly AccessTools.FieldRef<ParticleGeneric, uint> Direction = AccessTools.FieldRefAccess<ParticleGeneric, uint>("dirNormalized");
    private static readonly MethodInfo Produce = AccessTools.Method(typeof(ParticleGeneric), nameof(ParticleGeneric.UpdateBuffers))!;
    private static readonly AccessTools.FieldRef<ParticlePoolQuads, ClientMain> Game = AccessTools.FieldRefAccess<ParticlePoolQuads, ClientMain>("game");
    private static readonly MethodInfo InstanceUpdate = AccessTools.Method(typeof(ClientPlatformWindows), nameof(ClientPlatformWindows.UpdateMesh), [typeof(MeshRef), typeof(MeshData)])!;
    private static readonly MethodInfo[] Roots = [AccessTools.Method(typeof(ParticlePoolQuads), nameof(ParticlePoolQuads.OnNewFrame))!,
        AccessTools.Method(typeof(ParticlePoolQuads), nameof(ParticlePoolQuads.OnNewFrameOffThread))!];
    [ThreadStatic] private static bool scope;
    private static bool armed, ready;
    private static long deferredProperties, cpuFacadeChecks;
    private static int cpuFacadeValid = 1;
    internal static long DeferredProperties => Interlocked.Read(ref deferredProperties);
    internal static long CpuFacadeChecks => Interlocked.Read(ref cpuFacadeChecks);
    internal static bool CpuFacadeValid => Volatile.Read(ref cpuFacadeValid) != 0;
    private static int checkedFunctions;
    private static float[]? trigonometry;
    internal static float[] Trigonometry => trigonometry ??= ReadTrigonometry();
    private static bool Unset(EvolvingNatFloat value) => value.Transform == EvolvingNatFloat.NoValueSet.Transform && value.Factor == EvolvingNatFloat.NoValueSet.Factor;
    internal static PerformanceFeature Create() => new("gpu-particle-visuals", settings => settings.GpuParticleVisualSimulation,
        () => { _ = Trigonometry; }, harmony =>
        {
            foreach (var root in Roots) harmony.Patch(root,
                prefix: new HarmonyMethod(typeof(GpuParticleVisuals), nameof(BeforePool)),
                finalizer: new HarmonyMethod(typeof(GpuParticleVisuals), nameof(AfterPool)));
            harmony.Patch(Produce, prefix: new HarmonyMethod(typeof(GpuParticleVisuals), nameof(BeforeParticle)),
                postfix: new HarmonyMethod(typeof(GpuParticleVisuals), nameof(AfterParticle)));
            armed = true;
        }, Uninstall: () => { armed = ready = false; }, WorldReady: _ =>
        {
            ready = false;
            foreach (var method in Roots.Append(Produce).Concat(new[] {
                AccessTools.Method(typeof(EvolvingNatFloat), nameof(EvolvingNatFloat.nextFloat))!,
                AccessTools.Method(typeof(GameMath), nameof(GameMath.FastSin))!, AccessTools.Method(typeof(GameMath), nameof(GameMath.FastCos))! }))
                if (Harmony.GetPatchInfo(method)?.Owners.Any(owner => !PerformancePatchSet.IsOwnOwner(owner)) == true) return;
            ready = true;
        }, WorldLeft: () => ready = false);

    private static void BeforePool(ParticlePoolQuads __instance, out bool __state)
    {
        __state = scope;
        scope = armed && ready && PerformancePatchSet.Current?.GpuParticleVisualSimulation == true &&
            (__instance.GetType() == typeof(ParticlePoolQuads) || __instance.GetType() == typeof(ParticlePoolCubes)) &&
            Game(__instance).Platform.GetType() == typeof(ClientPlatformWindows) && InstanceUpdate != null &&
            Harmony.GetPatchInfo(InstanceUpdate)?.Owners.Any(owner => !PerformancePatchSet.IsOwnOwner(owner)) != true;
    }
    private static Exception? AfterPool(bool __state, Exception? __exception) { scope = __state; return __exception; }

    private static bool Capture(EvolvingNatFloat value, float first, float phase, bool defaultIsConstant, bool byteOutput, out ParticleEvolution result)
    {
        result = default;
        if (!float.IsFinite(first) || !float.IsFinite(value.Factor) || Math.Abs(value.Factor) > 1000000 ||
            !float.IsFinite(phase) || phase < 0 || phase > 2 || (value.MaxValue.HasValue && !float.IsFinite(value.MaxValue.Value))) return false;
        if (Unset(value))
        { result = ParticleEvolution.Constant(defaultIsConstant ? first : 0); return true; }
        int function = (int)value.Transform;
        if (function is < 0 or > 11 || (function is 3 or 4 && first == 0) || (function == 7 && value.Factor * phase < 0) ||
            (function == 6 && 1 + value.Factor * phase == 0)) return false;
        float x = value.Factor * phase;
        if (byteOutput && ((value.MaxValue.HasValue && Math.Abs(value.MaxValue.Value) > 1000000000) ||
            (function == 5 && Math.Abs(x) > 32768) || (function == 11 && Math.Abs(x) > 512) ||
            (function == 6 && Math.Abs(1 + x) < 0.000001f))) return false;
        result = new ParticleEvolution(first, value.Factor, phase, value.MaxValue ?? 0, function, value.MaxValue.HasValue);
        int bit = 1 << (function + (value.MaxValue.HasValue ? 12 : 0));
        if (HeadlessHarnessOptions.Enabled && (Interlocked.Or(ref checkedFunctions, bit) & bit) == 0)
        {
            if (result.Evaluate(Trigonometry) != value.nextFloat(first, phase)) Volatile.Write(ref cpuFacadeValid, 0);
            Interlocked.Increment(ref cpuFacadeChecks);
        }
        return true;
    }

    private static bool BeforeParticle(ParticleGeneric __instance, MeshData __0, Vec3d __1,
        ref int __2, ref int __3, ref int __4, out bool __state)
    {
        __state = false;
        if (!scope) { Materialize(__0); return true; }
        int index = __2 / 4;
        if (index >= 65536 || __2 < 0 || __2 % 4 != 0 || __3 != index * 12 || __4 != index ||
            __0.CustomFloats?.Values is not { } floats || __0.CustomBytes?.Values is not { } bytes ||
            __0.Flags is not { } flags || __2 + 4 > floats.Length || __3 + 12 > bytes.Length || __4 >= flags.Length) return true;
        var particle = __instance;
        if (Unset(particle.SizeEvolve) && Unset(particle.OpacityEvolve) && Unset(particle.RedEvolve) && Unset(particle.GreenEvolve) && Unset(particle.BlueEvolve)) return true;
        float phase = particle.SecondsAlive / particle.LifeLength;
        Span<ParticleEvolution> properties = stackalloc ParticleEvolution[5];
        if (!Capture(particle.SizeEvolve, particle.SizeMultiplier, phase, true, false, out properties[0]) ||
            !Capture(particle.OpacityEvolve, particle.ColorAlpha, phase, true, true, out properties[1]) ||
            !Capture(particle.RedEvolve, particle.ColorRed, phase, false, true, out properties[2]) ||
            !Capture(particle.GreenEvolve, particle.ColorGreen, phase, false, true, out properties[3]) ||
            !Capture(particle.BlueEvolve, particle.ColorBlue, phase, false, true, out properties[4])) return true;
        var batch = batches.GetValue(__0, _ => new Batch());
        if (batch.Properties.Length == 0)
            for (int prior = 0; prior < index; prior++)
            { batch.Slot(prior, floats.Length / 4); Fallback(batch, prior, floats, bytes); }
        batch.Slot(index, floats.Length / 4);
        properties.CopyTo(batch.Properties.AsSpan(index * 5, 5)); batch.Deferred++;
        float remainder = 1 - particle.prevPosAdvance;
        floats[__2++] = (float)(particle.Position.X - (double)(particle.prevPosDeltaX * remainder) - __1.X);
        floats[__2++] = (float)(particle.Position.Y - (double)(particle.prevPosDeltaY * remainder) - __1.Y);
        floats[__2++] = (float)(particle.Position.Z - (double)(particle.prevPosDeltaZ * remainder) - __1.Z);
        floats[__2++] = particle.SizeMultiplier;
        uint direction = Direction(particle);
        bytes[__3++] = (byte)direction; bytes[__3++] = (byte)(direction >> 8); bytes[__3++] = (byte)(direction >> 16); __3++;
        int light = particle.lightrgbs;
        bytes[__3++] = (byte)light; bytes[__3++] = (byte)(light >> 8); bytes[__3++] = (byte)(light >> 16); bytes[__3++] = (byte)(light >> 24);
        bytes[__3++] = particle.ColorBlue; bytes[__3++] = particle.ColorGreen; bytes[__3++] = particle.ColorRed; bytes[__3++] = particle.ColorAlpha;
        flags[__4++] = particle.VertexFlags;
        __state = true; Interlocked.Add(ref deferredProperties, 5); return false;
    }

    private static void AfterParticle(MeshData __0, int __2, bool __state)
    {
        if (__state || !scope || __2 < 4 || __2 % 4 != 0 || __0.CustomFloats?.Values is not { } floats ||
            __0.CustomBytes?.Values is not { } bytes || __2 > floats.Length) return;
        int index = __2 / 4 - 1;
        if (index >= 65536 || index * 12 + 11 >= bytes.Length) return;
        if (!batches.TryGetValue(__0, out var batch)) return;
        batch.Slot(index, floats.Length / 4); Fallback(batch, index, floats, bytes);
    }
    private static void Fallback(Batch batch, int index, float[] floats, byte[] bytes)
    {
        batch.Properties[index * 5] = ParticleEvolution.Constant(floats[index * 4 + 3]);
        batch.Properties[index * 5 + 1] = ParticleEvolution.Constant(bytes[index * 12 + 11]);
        for (int i = 2; i < 5; i++) batch.Properties[index * 5 + i] = ParticleEvolution.Constant(0);
    }

    internal static Batch? Pending(MeshData data) => batches.TryGetValue(data, out var batch) && batch.Pending &&
        batch.Count == data.CustomFloats?.Count / 4 ? batch : null;
    internal static ParticleEvolution SizeFor(MeshData data, int index, float fallback) =>
        Pending(data) is { } batch && index < batch.Count ? batch.Properties[index * 5] : ParticleEvolution.Constant(fallback);
    internal static float Evaluate(ParticleEvolution value)
    {
        if (value.Function == -1) return value.First;
        float result = EvolvingNatFloat.create((EnumTransformFunction)value.Function, value.Factor).nextFloat(value.First, value.Phase);
        return value.Limited != 0 ? Math.Min(value.Maximum, result) : result;
    }
    internal static void Materialize(MeshData data)
    {
        if (!batches.TryGetValue(data, out var batch) || !batch.Pending || data.CustomFloats?.Values is not { } floats ||
            data.CustomBytes?.Values is not { } bytes) return;
        if (batch.Deferred == 0) { batch.Pending = false; return; }
        int count = Math.Min(batch.Count, Math.Min(floats.Length / 4, bytes.Length / 12));
        for (int i = 0; i < count; i++)
        {
            int property = i * 5, color = i * 12 + 8;
            floats[i * 4 + 3] = Evaluate(batch.Properties[property]);
            bytes[color] = unchecked((byte)(bytes[color] + Evaluate(batch.Properties[property + 4])));
            bytes[color + 1] = unchecked((byte)(bytes[color + 1] + Evaluate(batch.Properties[property + 3])));
            bytes[color + 2] = unchecked((byte)(bytes[color + 2] + Evaluate(batch.Properties[property + 2])));
            bytes[color + 3] = (byte)Math.Clamp(Evaluate(batch.Properties[property + 1]), 0, 255);
        }
        batch.Pending = false;
    }

    private static float[] ReadTrigonometry()
    {
        RuntimeHelpers.RunClassConstructor(typeof(GameMath).TypeHandle);
        var sine = (float[])AccessTools.Field(typeof(GameMath), "sinValues").GetValue(null)!;
        var cosine = (float[])AccessTools.Field(typeof(GameMath), "cosValues").GetValue(null)!;
        if (sine.Length != 4096 || cosine.Length != 4096) throw new InvalidOperationException("Particle evolution table layout changed.");
        var values = new float[8193]; sine.CopyTo(values, 0); cosine.CopyTo(values, 4096);
        values[8192] = (float)AccessTools.Field(typeof(GameMath), "radToIndex").GetValue(null)!;
        return values;
    }
}
