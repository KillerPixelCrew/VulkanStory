using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Reflection;
using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game.Performance;

/// <summary>Opt-in timing events consumed alongside CLR stack samples by the out-of-process performance harness.</summary>
[EventSource(Name = "VulkanStory-Performance")]
internal sealed class PerformanceTrace : EventSource
{
    internal static readonly PerformanceTrace Log = new();
    internal readonly record struct Stamp(long Started, long Allocated);
    private RendererSettings? previousSettings;
    [NonEvent] internal void CaptureSettings(RendererSettings settings)
    {
        if (!IsEnabled()) { previousSettings = null; return; }
        if (ReferenceEquals(previousSettings, settings)) return;
        previousSettings = settings;
        CapturedSettings(System.Text.Json.JsonSerializer.Serialize(settings));
    }
    [NonEvent] internal Stamp Begin() => IsEnabled() ? new(Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread()) : default;
    [NonEvent] internal void End(string scope, Stamp stamp)
    {
        if (stamp.Started == 0 || !IsEnabled()) return;
        Scope(scope, (Stopwatch.GetTimestamp() - stamp.Started) * 1000d / Stopwatch.Frequency,
            GC.GetAllocatedBytesForCurrentThread() - stamp.Allocated);
    }
    [Event(1, Level = EventLevel.Informational)]
    public void Scope(string scope, double durationMs, long allocatedBytes) => WriteEvent(1, scope, durationMs, allocatedBytes);
    [Event(2, Level = EventLevel.Informational)]
    public void CapturedSettings(string json) => WriteEvent(2, json);

    internal static PerformanceFeature Create() => new("performance-harness", _ => true, () => { }, harmony =>
    {
        // Timing is dormant until EventPipe enables our provider. No reflection or allocation on inactive calls.
        foreach (Type type in typeof(ClientMain).Assembly.GetTypes().Where(t => typeof(ClientSystem).IsAssignableFrom(t)))
            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(type).Where(m => !m.IsAbstract && m.GetMethodBody() != null &&
                m.Name is "OnBeforeRender" or "OnRenderFrame" or "OnRenderFrameShadows"))
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(PerformanceTrace), nameof(Prefix)),
                    finalizer: new HarmonyMethod(typeof(PerformanceTrace), nameof(Finalizer)));
        Type? physics = typeof(ClientMain).Assembly.GetType("Vintagestory.Server.PhysicsManager");
        MethodInfo? tick = physics == null ? null : AccessTools.Method(physics, "ServerTick", [typeof(float)]);
        if (tick != null) harmony.Patch(tick, prefix: new HarmonyMethod(typeof(PerformanceTrace), nameof(Prefix)),
            finalizer: new HarmonyMethod(typeof(PerformanceTrace), nameof(Finalizer)));
    });
    private static void Prefix(out Stamp __state) => __state = Log.Begin();
    private static void Finalizer(MethodBase __originalMethod, Stamp __state)
    {
        if (__state.Started != 0) Log.End(__originalMethod.DeclaringType!.FullName + "." + __originalMethod.Name, __state);
    }
}
