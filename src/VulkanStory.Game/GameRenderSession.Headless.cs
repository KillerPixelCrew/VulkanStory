using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using SkiaSharp;

namespace VulkanStory.Game;

// Session-owned migration of ClientPlatformWindows.OptimumHeadlessTick/commands/capture.
// Baseline 386e0d05386d0b228b439d09aeca851428f7bbf3.
internal sealed partial class GameRenderSession
{
    private long headlessWorldFrame = -1;
    private int headlessWritten;
    private bool headlessCommandsDone, headlessDone;
    private bool headlessLegacyReady;
    private string? headlessCompletionReason;
    private bool headlessParityDone;
    private readonly Stopwatch headlessDeadline = Stopwatch.StartNew();
    private readonly int headlessTimeout = int.TryParse(Environment.GetEnvironmentVariable("VULKANSTORY_HEADLESS_TIMEOUT"), out var seconds)
        ? Math.Clamp(seconds, 10, 3600) : 300;

    private void PrepareHeadlessFrame(ref float delta)
    {
        if (!HeadlessHarnessOptions.Active) return;
        if (HeadlessHarnessOptions.Enabled && headlessDeadline.Elapsed.TotalSeconds >= headlessTimeout)
        { FailHeadlessRun("timeout", "renderCycle"); return; }
        if (HeadlessHarnessOptions.MainMenuOptions) { PrepareMainOptionsDiagnostic(); return; }
        if (HeadlessGameBindings.CurrentRunningClient() is not { BlocksReceivedAndLoaded: true } game) return;
        if (HeadlessHarnessOptions.FixedDeltaTime > 0)
        { delta = HeadlessHarnessOptions.FixedDeltaTime; game.DeltaTimeLimiter = delta; }
        headlessWorldFrame++;
        if (headlessCommandsDone || headlessWorldFrame < HeadlessHarnessOptions.CommandFrame) return;
        headlessCommandsDone = true;
        foreach (string line in HeadlessHarnessOptions.ReadCommands())
        {
            try
            {
                if (line.StartsWith(Vintagestory.Common.ChatCommandApi.ClientCommandPrefix, StringComparison.Ordinal))
                {
                    string rest = line[1..]; int space = rest.IndexOf(' ');
                    // The public property exposes the same official dispatcher.
                    // Retain its client overload and notification/caller behavior.
                    ((Vintagestory.Common.ChatCommandApi)game.api.ChatCommands).Execute(space > 0 ? rest[..space] : rest,
                        game.player, game.currentGroupid, space > 0 ? rest[(space + 1)..] : "", null);
                }
                else game.api.SendChatMessage(line, game.currentGroupid, null);
            }
            catch (Exception error) { platform.Logger.Error("VulkanStory headless command failed: {0}: {1}", line, error.Message); }
        }
    }

    /// <summary>Reads and writes configured headless frame output only after the harness readiness/warmup boundaries are satisfied.</summary>
    private void CaptureHeadlessFrame()
    {
        if (HeadlessHarnessOptions.MainMenuOptions) { CaptureMainOptionsDiagnostic(); return; }
        if (headlessDone || headlessLegacyReady || headlessWorldFrame < 0) return;
        if (HeadlessParityDump.Enabled && !headlessParityDone && headlessWorldFrame == HeadlessParityDump.Frame)
        { DumpHeadlessAttachments(HeadlessParityDump.Directory!); headlessParityDone = true; }
        if (!HeadlessHarnessOptions.CaptureEnabled)
        {
            if (HeadlessParityDump.Enabled && headlessParityDone) CompleteHeadless("attachment dump complete", true);
            else if (HeadlessHarnessOptions.Scenario != null && !HeadlessParityDump.Enabled)
                CompleteHeadless("no legacy captures requested", true);
            return;
        }
        if (HeadlessHarnessOptions.ShouldCapture(headlessWorldFrame))
        {
            if (WriteCapturedFrame(headlessWorldFrame)) headlessWritten++;
            if (HeadlessParityDump.AmbientOcclusionOutputs) DumpHeadlessAo(HeadlessHarnessOptions.FrameDirectory!, "frame" + headlessWorldFrame.ToString("D6"));
        }
        if (HeadlessHarnessOptions.CaptureFinished(headlessWorldFrame) && (!HeadlessParityDump.Enabled || headlessParityDone))
            CompleteHeadless("capture complete", headlessWritten == HeadlessHarnessOptions.Frames.Length);
    }

    private void DumpHeadlessAttachments(string directory,
        Dictionary<string, (int Width, int Height)>? dimensions = null,
        GameTemporalFrame? scenarioFrame = null, ulong? scenarioCaptureFrameId = null,
        bool? scenarioCurrentWorldSample = null, int? scenarioWidth = null, int? scenarioHeight = null)
    {
        Directory.CreateDirectory(directory);
        if (scenarioFrame is { } raw && scenarioCaptureFrameId is ulong captureFrameId &&
            scenarioCurrentWorldSample is bool currentWorldSample &&
            scenarioWidth is int width && scenarioHeight is int height)
            WriteScenarioFrameInputs(directory, captureFrameId, raw, currentWorldSample, width, height);
        else
        {
            // Sample the attachment boundary, rather than the one-second status tick.
            var frame = Temporal.Snapshot();
            File.WriteAllText(Path.Combine(directory, "frame-inputs.json"), JsonSerializer.Serialize(new
            {
                frameId = Device.LatencyFrameId, worldFrame = headlessWorldFrame,
                temporalFrameId = frame.FrameId,
                frame.WorldCaptured, frame.MotionValid, frame.HasCamera,
                jitterX = frame.Provider.JitterX, jitterY = frame.Provider.JitterY,
                reset = frame.Provider.Reset, renderedDeltaTimeMs = frame.Provider.DeltaTimeMs,
                gameDitherSeed = platform.ShaderUniforms.DitherSeed,
                gameFrameWidth = platform.ShaderUniforms.FrameWidth,
                upscaler = services.RendererSettings.EffectiveUpscaler,
                upscaleEvaluated = Graphics.UpscaledThisFrame,
                skyMotion = Graphics.SkyMotionCapture
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
            }));
        }
        for (int slot = 0; slot < platform.FrameBuffers.Count; slot++)
        {
            var target = platform.FrameBuffers[slot];
            if (target == null || target.Disposed) continue;
            string name = Enum.IsDefined(typeof(EnumFrameBuffer), slot) ? ((EnumFrameBuffer)slot).ToString() : "Slot" + slot;
            if (target.ColorTextureIds is { } colors)
                for (int attachment = 0; attachment < colors.Length; attachment++)
                    WriteHeadlessAttachment(directory, slot, name, "color" + attachment, colors[attachment], dimensions);
            WriteHeadlessAttachment(directory, slot, name, "depth", target.DepthTextureId, dimensions);
        }
        if (HeadlessParityDump.AmbientOcclusionOutputs) DumpHeadlessAo(directory, "color0", dimensions);
    }

    private static void WriteHeadlessPng(long frame, int width, int height, byte[] bgra) =>
        WriteHeadlessPng(Path.Combine(HeadlessHarnessOptions.FrameDirectory!,
            Path.ChangeExtension(HeadlessHarnessOptions.FrameFileName(frame), ".png")), width, height, bgra);

    private static void WriteHeadlessPng(string path, int width, int height, byte[] bgra)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        int rowBytes = checked(width * 4);
        // PPM retains GL row order for comparisons; PNG is top-down for viewing.
        for (int row = 0; row < height; row++)
            Marshal.Copy(bgra, row * rowBytes, IntPtr.Add(bitmap.GetPixels(), (height - 1 - row) * bitmap.RowBytes), rowBytes);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100) ??
            throw new InvalidOperationException("Headless PNG encoding failed.");
        using var output = File.Create(path);
        encoded.SaveTo(output);
    }

    private void DumpHeadlessAo(string directory, string attachment,
        Dictionary<string, (int Width, int Height)>? dimensions = null)
    {
        string[] names = ["AoWorking", "AoEdges", "AoDepthMip0", "AoOutput"];
        for (int index = 0; index < names.Length; index++)
            WriteHeadlessAttachment(directory, 40 + index, names[index], attachment,
                Graphics.AmbientOcclusionDebugTexture(index), dimensions);
    }

    private void WriteHeadlessAttachment(string directory, int slot, string name, string attachment, int texture,
        Dictionary<string, (int Width, int Height)>? dimensions = null)
    {
        if (texture <= 0) return;
        var readback = Device.ReadTextureForParity(texture) ?? throw new InvalidOperationException("Headless attachment cannot be read: " + slot + "/" + attachment);
        if (HeadlessParityDump.Write(directory, slot, name, attachment, readback) == 0)
            throw new InvalidOperationException("Headless attachment write failed: " + slot + "/" + attachment);
        if (dimensions != null)
        {
            string prefix = slot + "-" + name + "-" + attachment + "-";
            foreach (string path in Directory.EnumerateFiles(directory).Where(path =>
                         Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal)))
                dimensions[Path.GetFullPath(path)] = (readback.Width, readback.Height);
        }
    }

    private void CompleteHeadless(string reason, bool success)
    {
        if (!success)
        {
            FailHeadlessRun(reason, HeadlessHarnessOptions.Scenario == null ? "renderCycle" : "preGenerateReadback");
            return;
        }
        if (headlessDone || headlessLegacyReady) return;
        if (MultiplierDiagnosticRequested && !multiplierDiagnosticComplete)
        {
            FailHeadlessRun("Multiplier drag/Save acknowledgement is missing.", "optionsDiagnostic");
            return;
        }
        headlessCompletionReason = reason;
        headlessLegacyReady = true;
    }

    private void ThrottleHeadlessFrame()
    {
        if (!HeadlessHarnessOptions.Enabled) return;
        int wait = 33 - (int)frameClock.ElapsedMilliseconds;
        if (wait > 0) Thread.Sleep(wait);
    }
}
