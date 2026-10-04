using System.Diagnostics;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Game;

internal sealed partial class GameRenderSession
{
    private long realPresents, sdkPresents, fpsSampleTicks, previousRealPresents, previousSdkPresents;
    private long sdkReports, previousSdkReports;
    private long hostGeneratedPresents, previousHostGeneratedPresents;
    private string fpsProvider = "off", fpsText = "Real: -- FPS | FG output: -- FPS";
    internal string FpsText => Volatile.Read(ref fpsText);
    internal bool ShowFpsCounter => services.RendererSettings.Settings.ShowFpsCounter;
    private void AttachPresentationCounters() => VulkanStats.ConfigurePresentationObserver(
        () => Interlocked.Increment(ref realPresents), count =>
        { Interlocked.Add(ref sdkPresents, count); Interlocked.Increment(ref sdkReports); },
        () => Interlocked.Increment(ref hostGeneratedPresents));
    private void SampleFps()
    {
        long now = Stopwatch.GetTimestamp(), real = Interlocked.Read(ref realPresents), output = Interlocked.Read(ref sdkPresents);
        string provider = frameGeneration!.EffectiveProvider;
        long reports = Interlocked.Read(ref sdkReports);
        long generated = Interlocked.Read(ref hostGeneratedPresents);
        if (!ShowFpsCounter) { fpsSampleTicks = 0; return; }
        if (fpsSampleTicks == 0 || fpsProvider != provider)
        {
            fpsSampleTicks = now; previousRealPresents = real; previousSdkPresents = output; previousSdkReports = reports; fpsProvider = provider;
            previousHostGeneratedPresents = generated;
            Volatile.Write(ref fpsText, provider == "off" ? "Real: -- FPS | FG: Off" : "Real: -- FPS | FG output: -- FPS");
            return;
        }
        double seconds = (now - fpsSampleTicks) / (double)Stopwatch.Frequency;
        if (seconds < 1) return;
        double realFps = (real - previousRealPresents) / seconds, outputFps = (output - previousSdkPresents) / seconds;
        string outputLabel = reports == previousSdkReports ? "-- (not reported)" : $"{outputFps:0} FPS";
        if (provider == "fsr3" && Device.Fsr3DirectPresentation)
            outputLabel = $"{(real - previousRealPresents + generated - previousHostGeneratedPresents) / seconds:0} FPS";
        Volatile.Write(ref fpsText, provider == "off" ? $"Real: {realFps:0} FPS | FG: Off" : $"Real: {realFps:0} FPS | FG output: {outputLabel}");
        fpsSampleTicks = now; previousRealPresents = real; previousSdkPresents = output; previousSdkReports = reports;
        previousHostGeneratedPresents = generated;
    }
}
