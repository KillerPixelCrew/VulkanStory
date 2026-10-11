using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VulkanStory.Game.Performance;

/// <summary>Ports Optimum's wind lookup throttle without throttling the game's smoothing or weather simulation.</summary>
internal static class WeatherWindSampleCache
{
    private sealed class Sample
    {
        internal double X, Y, Z;
        internal long Stamp;
        internal Vec3d? Wind;
    }
    private sealed class State { internal readonly Sample[] Samples = [new(), new()]; internal int Next; }
    private static ConditionalWeakTable<IBlockAccessor, State> states = new();
    private static readonly MethodInfo Lookup = AccessTools.Method(typeof(IBlockAccessor), "GetWindSpeedAt", [typeof(Vec3d)])!;

    internal static PerformanceFeature Create(Assembly essentials)
    {
        MethodInfo? target = null;
        return new("weather-wind-sample-cache", s => s.WeatherWindSampleCache, () =>
        {
            Type weather = essentials.GetType("Vintagestory.GameContent.WeatherSystemClient", true)!;
            target = AccessTools.Method(weather, "OnRenderFrame", [typeof(float), typeof(EnumRenderStage)]);
            if (target == null || Lookup == null || PatchProcessor.GetOriginalInstructions(target).Count(i => i.Calls(Lookup)) != 2)
                throw new InvalidOperationException("Weather wind lookup anchors changed.");
        }, harmony => harmony.Patch(target!, transpiler: new HarmonyMethod(typeof(WeatherWindSampleCache), nameof(Transpiler))),
            WorldLeft: () => states = new());
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(Lookup))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(WeatherWindSampleCache), nameof(GetWind));
            }
            yield return instruction;
        }
    }
    private static Vec3d GetWind(IBlockAccessor accessor, Vec3d position)
    {
        if (PerformancePatchSet.Current?.WeatherWindSampleCache != true) return accessor.GetWindSpeedAt(position);
        State state = states.GetOrCreateValue(accessor);
        long now = Stopwatch.GetTimestamp();
        foreach (Sample sample in state.Samples)
        {
            double dx = position.X - sample.X, dy = position.Y - sample.Y, dz = position.Z - sample.Z;
            if (sample.Wind != null && now - sample.Stamp < Stopwatch.Frequency / 20 && dx * dx + dy * dy + dz * dz < .0625)
                return sample.Wind;
        }
        Sample next = state.Samples[state.Next++ & 1];
        next.X = position.X; next.Y = position.Y; next.Z = position.Z; next.Stamp = now;
        Vec3d wind = accessor.GetWindSpeedAt(position);
        next.Wind ??= new Vec3d();
        next.Wind.Set(wind.X, wind.Y, wind.Z);
        return next.Wind;
    }
}
