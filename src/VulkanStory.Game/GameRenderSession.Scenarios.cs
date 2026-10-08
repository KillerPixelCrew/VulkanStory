using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vintagestory.API.Client;

namespace VulkanStory.Game;

internal sealed partial class GameRenderSession
{
    private readonly HashSet<int> scenarioExecutedActions = new();
    private readonly Dictionary<string, ScenarioCheckpoint> scenarioCheckpoints = new(StringComparer.Ordinal);
    private readonly List<ScenarioCaptureReceipt> scenarioCaptures = new();
    private string? scenarioSessionId;
    private int? scenarioTick;
    private string scenarioFailurePhase = "renderCycle";
    private string? scenarioFailure;
    private bool scenarioTerminal;
    private int scenarioWrittenCaptures, scenarioPairedCaptures;
    private ulong? lastSuccessfulScenarioFrameId;
    private string ScenarioSessionId => scenarioSessionId ??= Guid.NewGuid().ToString("N");
    private string ScenarioOutputDirectory => HeadlessHarnessOptions.FrameDirectory
        ?? throw new InvalidOperationException("Scenario output directory is not configured.");

    private sealed record ScenarioFile(string Path, string Sha256, int? Width, int? Height);
    private sealed record ScenarioObservedField(object? Value, string Scope, ulong? SourceFrameId,
        ulong? SampledAtFrameId, string SessionId);
    private sealed record ScenarioCheckpoint(string Name, string SessionId,
        IReadOnlyDictionary<string, ScenarioObservedField> Fields);

    private sealed class ScenarioCaptureReceipt(
        HeadlessScenarioAction action,
        int tick,
        ulong captureFrameId,
        long worldFrame,
        ulong rawTemporalFrameId,
        bool hasCurrentWorldSample,
        IReadOnlyList<ScenarioFile> files)
    {
        internal HeadlessScenarioAction Action { get; } = action;
        internal int Tick { get; } = tick;
        internal ulong CaptureFrameId { get; } = captureFrameId;
        internal long WorldFrame { get; } = worldFrame;
        internal ulong RawTemporalFrameId { get; } = rawTemporalFrameId;
        internal bool HasCurrentWorldSample { get; } = hasCurrentWorldSample;
        internal IReadOnlyList<ScenarioFile> Files { get; } = files;
        internal ulong? CompletedFrameId { get; set; }
    }

    private void AdvanceHeadlessScenarioBeforeInput()
    {
        HeadlessScenario? scenario = HeadlessHarnessOptions.Scenario;
        if (scenario == null || scenarioTerminal || headlessDone) return;
        if (scenarioTick == null)
        {
            try { if (!TryStartScenarioContext(scenario)) return; }
            catch (Exception error) { FailHeadlessRun(error.GetBaseException().Message, "beforeInput"); return; }
            scenarioTick = 0;
        }
        else scenarioTick++;

        try { RequireScenarioContext(); }
        catch (Exception error) { FailHeadlessRun(error.GetBaseException().Message, "beforeInput"); return; }

        int tick = scenarioTick.Value;
        HeadlessScenarioAction? missed = scenario.Actions.FirstOrDefault(action =>
            action.Tick < tick && !scenarioExecutedActions.Contains(action.Index));
        if (missed != null)
        {
            try
            {
                WriteScenarioEvent(missed, ScenarioActionPhase(missed.Kind), true, false,
                    "missed declared tick", "Action was not executed on its declared scenario tick.");
            }
            catch { }
            FailHeadlessRun("Action missed its declared scenario tick: " + missed.Id, "settings");
            return;
        }

        foreach (HeadlessScenarioAction action in scenario.Actions.Where(action =>
                     action.Tick == tick && action.Kind is HeadlessScenarioActionKind.Settings or
                         HeadlessScenarioActionKind.Options or HeadlessScenarioActionKind.Resize))
        {
            string phase = ScenarioActionPhase(action.Kind);
            scenarioFailurePhase = phase;
            try
            {
                if (action.Kind == HeadlessScenarioActionKind.Settings)
                {
                    JsonObject current = JsonNode.Parse(RuntimeBootstrap.Current.ReadSettings())?.AsObject()
                        ?? throw new InvalidOperationException("Runtime settings read returned no object.");
                    foreach (JsonProperty setting in action.Values.EnumerateObject())
                        current[setting.Name] = JsonNode.Parse(setting.Value.GetRawText());
                    string? error = RuntimeBootstrap.Current.SaveSettings(current.ToJsonString());
                    if (error != null) throw new InvalidOperationException(error);
                }
                else if (action.Kind == HeadlessScenarioActionKind.Options) ExecuteScenarioOptions(action);
                else Window.SetSize(action.Width, action.Height); // SDL events own resize and GUI recomposition.
                if (!scenarioExecutedActions.Add(action.Index))
                {
                    FailHeadlessRun("Pre-input action was executed more than once: " + action.Id, phase);
                    return;
                }
                WriteScenarioEvent(action, phase, true, true, "accepted", null);
            }
            catch (Exception error)
            {
                try { WriteScenarioEvent(action, phase, false, false, null, error.GetBaseException().Message); }
                catch { }
                FailHeadlessRun("Pre-input action failed: " + error.GetBaseException().Message, phase);
                return;
            }
        }
        scenarioFailurePhase = "renderCycle";
    }

    private void WriteScenarioEvent(HeadlessScenarioAction action, string phase, bool accepted,
        bool executed, object? result, string? reason, ulong? captureFrameId = null,
        ulong? completedFrameId = null)
    {
        Directory.CreateDirectory(ScenarioOutputDirectory);
        var entry = new
        {
            declaredIndex = action.Index,
            actionId = action.Id,
            actionKind = action.Kind.ToString().ToLowerInvariant(),
            declaredTick = action.Tick,
            scenarioTick,
            phase,
            sessionId = ScenarioSessionId,
            captureFrameId,
            completedFrameId,
            accepted,
            executed,
            result,
            reason
        };
        File.AppendAllText(Path.Combine(ScenarioOutputDirectory, "scenario-events.jsonl"),
            JsonSerializer.Serialize(entry) + Environment.NewLine);
    }

    private static string ScenarioActionPhase(HeadlessScenarioActionKind kind) => kind switch
    {
        HeadlessScenarioActionKind.Settings => "settings",
        HeadlessScenarioActionKind.Options or HeadlessScenarioActionKind.Resize => "beforeInput",
        HeadlessScenarioActionKind.Capture => "preGenerateReadback",
        _ => "completedFrame"
    };

    private string ScenarioManifestPath => Path.Combine(ScenarioOutputDirectory, "scenario-captures.json");

    private void SaveScenarioCaptureManifest()
    {
        var manifest = new
        {
            schema = 1,
            sessionId = ScenarioSessionId,
            captures = scenarioCaptures.Select(capture => new
            {
                actionIndex = capture.Action.Index,
                actionId = capture.Action.Id,
                name = capture.Action.Name,
                attachments = capture.Action.Attachments,
                phase = "preGenerateReadback",
                sessionId = ScenarioSessionId,
                scenarioTick = capture.Tick,
                captureFrameId = capture.CaptureFrameId,
                completedFrameId = capture.CompletedFrameId,
                worldFrame = capture.WorldFrame,
                rawTemporalFrameId = capture.RawTemporalFrameId,
                hasCurrentWorldSample = capture.HasCurrentWorldSample,
                temporalFrameId = capture.HasCurrentWorldSample ? capture.RawTemporalFrameId : (ulong?)null,
                files = capture.Files
            }).ToArray()
        };
        WriteJsonAtomically(ScenarioManifestPath, manifest);
    }

    /// <summary>Writes requested scenario attachment readbacks at the scheduled post-render seam and records their file identities.</summary>
    private void CaptureScenarioReadbacks()
    {
        HeadlessScenario? scenario = HeadlessHarnessOptions.Scenario;
        if (scenario == null || scenarioTerminal || headlessDone || scenarioTick == null) return;
        scenarioFailurePhase = "preGenerateReadback";
        int tick = scenarioTick.Value;
        HeadlessScenarioAction? missed = scenario.Actions.FirstOrDefault(action =>
            action.Tick < tick && action.Kind == HeadlessScenarioActionKind.Capture &&
            !scenarioExecutedActions.Contains(action.Index));
        if (missed != null)
        {
            WriteScenarioEvent(missed, "preGenerateReadback", true, false,
                "missed declared tick", "Capture was not executed on its declared scenario tick.");
            FailHeadlessRun("Capture action missed its declared scenario tick: " + missed.Id, "preGenerateReadback");
            return;
        }
        foreach (HeadlessScenarioAction action in scenario.Actions.Where(action =>
                     action.Tick == tick && action.Kind == HeadlessScenarioActionKind.Capture))
        {
            try { CaptureScenario(action, tick); }
            catch (Exception error)
            {
                try { WriteScenarioEvent(action, "preGenerateReadback", false, false, null, error.Message,
                    Device.LatencyFrameId); }
                catch { }
                FailHeadlessRun("Scenario capture failed: " + error.Message, "preGenerateReadback");
                throw;
            }
            if (scenarioTerminal) return;
        }
        scenarioFailurePhase = "renderCycle";
    }

    private void CaptureScenario(HeadlessScenarioAction action, int tick)
    {
        ulong captureFrameId = Device.LatencyFrameId;
        var temporal = Temporal.Snapshot();
        bool currentWorldSample = temporal.WorldCaptured && temporal.FrameId == captureFrameId;
        string captureDirectory = Path.Combine(ScenarioOutputDirectory, "scenario-captures", action.Name!);
        Directory.CreateDirectory(captureDirectory);
        var captured = ReadCapturedFrame();
        var size = (captured.Width, captured.Height);
        byte[] pixels = captured.Pixels;

        string ppmPath = Path.Combine(captureDirectory, action.Name + ".ppm");
        string pngPath = Path.Combine(captureDirectory, action.Name + ".png");
        if (!HeadlessHarnessOptions.WriteFrame(ppmPath, size.Width, size.Height, pixels, true))
            throw new InvalidOperationException("Scenario PPM write failed.");
        WriteHeadlessPng(pngPath, size.Width, size.Height, pixels);
        var dimensions = new Dictionary<string, (int Width, int Height)>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.GetFullPath(ppmPath)] = (size.Width, size.Height),
            [Path.GetFullPath(pngPath)] = (size.Width, size.Height)
        };
        if (action.Attachments)
            DumpHeadlessAttachments(captureDirectory, dimensions, temporal, captureFrameId,
                currentWorldSample, size.Width, size.Height);

        var files = Directory.EnumerateFiles(captureDirectory, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path =>
            {
                dimensions.TryGetValue(Path.GetFullPath(path), out var fileSize);
                return new ScenarioFile(
                    Path.GetRelativePath(ScenarioOutputDirectory, path).Replace('\\', '/'),
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
                    fileSize.Width == 0 ? null : fileSize.Width,
                    fileSize.Height == 0 ? null : fileSize.Height);
            }).ToArray();
        var receipt = new ScenarioCaptureReceipt(action, tick, captureFrameId, headlessWorldFrame,
            temporal.FrameId, currentWorldSample, files);
        scenarioCaptures.Add(receipt);
        scenarioWrittenCaptures++;
        SaveScenarioCaptureManifest();
        if (!scenarioExecutedActions.Add(action.Index))
            throw new InvalidOperationException("Scenario capture action was executed more than once: " + action.Id);
        WriteScenarioEvent(action, "preGenerateReadback", true, true,
            new { fileCount = files.Length, currentWorldSample }, null, captureFrameId);
    }

    private void WriteScenarioFrameInputs(string directory, ulong captureFrameId,
        in GameTemporalFrame raw, bool currentWorldSample, int width, int height)
    {
        object? temporalFrameId = currentWorldSample ? raw.FrameId : null;
        FrameBufferRef? primary = platform.FrameBuffers is { Count: > 0 } targets &&
            targets[0] is { Disposed: false } target ? target : null;
        File.WriteAllText(Path.Combine(directory, "frame-inputs.json"), JsonSerializer.Serialize(new
        {
            phase = "preGenerateReadback",
            sessionId = ScenarioSessionId,
            captureFrameId,
            frameId = captureFrameId,
            worldFrame = headlessWorldFrame,
            temporalFrameId,
            hasCurrentWorldSample = currentWorldSample,
            WorldCaptured = currentWorldSample ? raw.WorldCaptured : (bool?)null,
            MotionValid = currentWorldSample ? raw.MotionValid : (bool?)null,
            HasCamera = currentWorldSample ? raw.HasCamera : (bool?)null,
            jitterX = currentWorldSample ? raw.Provider.JitterX : (float?)null,
            jitterY = currentWorldSample ? raw.Provider.JitterY : (float?)null,
            reset = currentWorldSample ? raw.Provider.Reset : (bool?)null,
            renderedDeltaTimeMs = currentWorldSample ? raw.Provider.DeltaTimeMs : (float?)null,
            gameDitherSeed = currentWorldSample ? (object?)platform.ShaderUniforms.DitherSeed : null,
            gameFrameWidth = currentWorldSample ? (object?)platform.ShaderUniforms.FrameWidth : null,
            upscaler = currentWorldSample ? services.RendererSettings.EffectiveUpscaler : null,
            upscaleEvaluated = currentWorldSample ? Graphics.UpscaledThisFrame : (bool?)null,
            skyMotion = currentWorldSample ? Graphics.SkyMotionCapture : null,
            renderWidth = primary?.Width,
            renderHeight = primary?.Height,
            displayWidth = width,
            displayHeight = height
        }, new JsonSerializerOptions
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
        }));
    }

    private void CompleteHeadlessScenarioFrame()
    {
        if (headlessDone) return;
        if (HeadlessHarnessOptions.Enabled && headlessDeadline.Elapsed.TotalSeconds >= headlessTimeout)
        {
            FailHeadlessRun("timeout", "completedFrame");
            return;
        }
        HeadlessScenario? scenario = HeadlessHarnessOptions.Scenario;
        if (scenario == null)
        {
            if (!headlessLegacyReady) return;
            scenarioFailurePhase = "completedFrame";
            (bool hidden, bool focused, bool stagedModLoaded, string modLocation) = CurrentScenarioInvariants();
            if (HeadlessHarnessOptions.Enabled && (!diagnosticWorldReady || !stagedModLoaded))
            { FailHeadlessRun("staged ModSystem or world-ready callback is missing", "completedFrame"); return; }
            if (HeadlessHarnessOptions.Enabled && (!hidden || focused))
            { FailHeadlessRun("headless window became visible or focused", "completedFrame"); return; }
            FinalizeHeadlessRun(true, headlessCompletionReason ?? "capture complete",
                hidden, focused, stagedModLoaded, modLocation);
            return;
        }
        if (scenarioTerminal || scenarioTick == null) return;

        scenarioFailurePhase = "completedFrame";
        int tick = scenarioTick.Value;
        ulong completedFrameId = Device.LatencyFrameId;
        foreach (ScenarioCaptureReceipt capture in scenarioCaptures.Where(receipt => receipt.CompletedFrameId == null))
        {
            if (capture.Tick != tick || capture.CaptureFrameId != completedFrameId)
            {
                WriteScenarioEvent(capture.Action, "completedFrame", true, true,
                    "capture frame pairing failed", "Pre-Generate capture frame did not pair with its completed CPU frame.",
                    capture.CaptureFrameId, completedFrameId);
                FailHeadlessRun("Pre-Generate capture frame did not pair with its completed CPU frame.", "completedFrame");
                return;
            }
            capture.CompletedFrameId = completedFrameId;
            scenarioPairedCaptures++;
        }
        SaveScenarioCaptureManifest();

        lastSuccessfulScenarioFrameId = completedFrameId;
        RequireScenarioContext();
        // Settings-file observations belong to requested checkpoints/assertions, not every provider frame.
        IReadOnlyDictionary<string, ScenarioObservedField>? observation = scenario.Actions.Any(action =>
            action.Tick == tick && action.Kind is HeadlessScenarioActionKind.Checkpoint or HeadlessScenarioActionKind.Assert)
            ? SnapshotScenarioFrame(tick, completedFrameId) : null;
        HeadlessScenarioAction? missed = scenario.Actions.FirstOrDefault(action =>
            action.Tick < tick && !scenarioExecutedActions.Contains(action.Index));
        if (missed != null)
        {
            WriteScenarioEvent(missed, "completedFrame", true, false,
                "missed declared tick", "Observation was not executed on its declared scenario tick.");
            FailHeadlessRun("Observation action missed its declared scenario tick: " + missed.Id, "completedFrame");
            return;
        }

        foreach (HeadlessScenarioAction action in scenario.Actions.Where(action =>
                     action.Tick == tick && action.Kind is HeadlessScenarioActionKind.Checkpoint or HeadlessScenarioActionKind.Assert))
        {
            if (action.Kind == HeadlessScenarioActionKind.Checkpoint)
            {
                var checkpoint = new ScenarioCheckpoint(action.Name!, ScenarioSessionId, observation!);
                if (!scenarioCheckpoints.TryAdd(action.Name!, checkpoint))
                {
                    FailHeadlessRun("Checkpoint was recorded more than once: " + action.Name, "completedFrame");
                    return;
                }
                if (!scenarioExecutedActions.Add(action.Index))
                {
                    FailHeadlessRun("Checkpoint action was executed more than once: " + action.Id, "completedFrame");
                    return;
                }
                WriteScenarioCheckpoint(action, tick, completedFrameId, observation!);
                WriteScenarioEvent(action, "completedFrame", true, true, "checkpoint recorded", null,
                    completedFrameId: completedFrameId);
            }
            else if (!EvaluateScenarioAssertion(action, tick, completedFrameId, observation!))
                return;
        }

        if (scenarioTerminal) return;
        bool allActionsExecuted = scenarioExecutedActions.Count == scenario.Actions.Count;
        if (!allActionsExecuted || !headlessLegacyReady) return;
        if (scenarioWrittenCaptures != scenario.Actions.Count(action => action.Kind == HeadlessScenarioActionKind.Capture) ||
            scenarioPairedCaptures != scenarioWrittenCaptures)
        {
            FailHeadlessRun("Scenario captures are incomplete or unpaired.", "completedFrame");
            return;
        }
        (bool scenarioHidden, bool scenarioFocused, bool staged, string location) = CurrentScenarioInvariants();
        bool expectedHidden = HeadlessHarnessOptions.KeepWindowHidden;
        bool contextReady = scenario.Context == "world" ? staged && diagnosticWorldReady
            : Temporal.CurrentClient == null && HeadlessGameBindings.CurrentRunningClient() == null && !diagnosticWorldReady;
        if (scenarioHidden != expectedHidden || scenarioFocused == expectedHidden || !contextReady)
        {
            FailHeadlessRun("Scenario terminal invariants failed.", "completedFrame");
            return;
        }
        FinalizeHeadlessRun(true, "scenario complete", scenarioHidden, scenarioFocused, staged, location,
            completedFrameId);
    }

    private IReadOnlyDictionary<string, ScenarioObservedField> SnapshotScenarioFrame(int tick, ulong completedFrameId)
    {
        var raw = Temporal.Snapshot();
        bool currentWorldSample = raw.WorldCaptured && raw.FrameId == completedFrameId;
        RendererSettings settings = services.RendererSettings.Settings;
        FrameBufferRef? primary = platform.FrameBuffers is { Count: > 0 } targets &&
            targets[0] is { Disposed: false } target ? target : null;
        var display = Window.PixelSize;
        var window = Window.WindowSize;
        var options = SnapshotScenarioOptions();
        DlssQueryObservation? query = frameGeneration!.HeadlessDlssQueryObservation;
        bool queryCurrent = query is { } q && q.QueryFrameId == completedFrameId;
        bool countersAvailable = !diagnosticsDisabled && !string.IsNullOrWhiteSpace(diagnosticDirectory) &&
            Path.IsPathFullyQualified(diagnosticDirectory);
        object? configuredDlssCount = frameGeneration.PreparedThisFrame &&
            frameGeneration.EffectiveProvider == "dlss" ? frameGeneration.ConfiguredDlssGeneratedFrames : null;
        object? queryFrameId = queryCurrent ? query!.Value.QueryFrameId : null;
        object? queryResult = queryCurrent ? query!.Value.Result : null;
        object? queryMaximum = queryCurrent ? query!.Value.State?.MaximumGenerated : null;
        object? queryDynamic = queryCurrent ? query!.Value.State?.DynamicMfgSupport : null;
        object? successfulUpscaleFrames = countersAvailable ? diagnosticUpscaleFrames : null;
        object? preparedFrames = countersAvailable ? diagnosticPreparedFrames : null;

        var fields = new Dictionary<string, ScenarioObservedField>(StringComparer.Ordinal);
        void Current(string name, object? value) =>
            fields[name] = new ScenarioObservedField(value, "current-cpu-frame", completedFrameId, completedFrameId, ScenarioSessionId);
        void Aggregate(string name, object? value) =>
            fields[name] = new ScenarioObservedField(value, "aggregate-at-sample", null, completedFrameId, ScenarioSessionId);

        Current("hidden", !Window.IsVisible);
        Current("focused", Window.IsFocused);
        Current("stagedModLoaded", HeadlessModDiscovery.IsStagedModLoaded(HeadlessModDiscovery.LoadedModLocation()));
        Current("worldReady", diagnosticWorldReady);
        Current("optionsHost", options.Host);
        Current("optionsPage", options.Page);
        Current("worldPaused", options.Paused);
        Current("pauseMenuOpen", options.PauseOpen);
        Current("requestedTaa", options.Requested);
        Current("appliedTaa", options.Applied);
        Current("persistedTaa", options.Persisted);
        Current("hasCurrentWorldSample", currentWorldSample);
        Current("WorldCaptured", currentWorldSample ? raw.WorldCaptured : null);
        Current("MotionValid", currentWorldSample ? raw.MotionValid : null);
        Current("HasCamera", currentWorldSample ? raw.HasCamera : null);
        Current("temporalReset", currentWorldSample ? raw.Provider.Reset : null);
        Current("temporalFrameId", currentWorldSample ? raw.FrameId : null);
        Current("requestedUpscaler", settings.Upscaler);
        Current("effectiveUpscaler", services.RendererSettings.EffectiveUpscaler);
        Current("requestedFrameGeneration", settings.FrameGeneration);
        Current("effectiveFrameGeneration", frameGeneration.EffectiveProvider);
        Current("inputsPreparedThisFrame", frameGeneration.PreparedThisFrame);
        Current("preparationStatus", frameGeneration.PreparationStatus);
        Current("renderWidth", primary?.Width);
        Current("renderHeight", primary?.Height);
        Current("displayWidth", display.Width);
        Current("displayHeight", display.Height);
        Current("windowWidth", window.Width);
        Current("windowHeight", window.Height);
        Current("configuredDlssGeneratedFrames", configuredDlssCount);
        Current("dlssStateQueryFrameId", queryFrameId);
        Current("dlssStateQueryResult", queryResult);
        Current("dlssMaximumGenerated", queryMaximum);
        Current("dlssDynamicMfgSupport", queryDynamic);
        Aggregate("successfulUpscaleFrames", successfulUpscaleFrames);
        Aggregate("preparedFrames", preparedFrames);
        Aggregate("realPresents", Interlocked.Read(ref realPresents));
        Aggregate("sdkReportedPresents", Interlocked.Read(ref sdkPresents));
        Aggregate("hostGeneratedPresents", Interlocked.Read(ref hostGeneratedPresents));
        Aggregate("sdkReportedDlssPresents", frameGeneration.ActualDlssPresents);
        Current("cpuRenderCycleSucceeded", true);
        Current("presentCallReturned", true);
        Current("scenarioTick", tick);
        Current("worldFrame", headlessWorldFrame);
        Current("sampledAtFrameId", completedFrameId);
        Current("completedFrameId", completedFrameId);
        Current("sessionId", ScenarioSessionId);
        Current("phase", "completedFrame");
        return fields;
    }

    private void WriteScenarioCheckpoint(HeadlessScenarioAction action, int tick, ulong completedFrameId,
        IReadOnlyDictionary<string, ScenarioObservedField> fields)
    {
        Directory.CreateDirectory(ScenarioOutputDirectory);
        var values = fields.ToDictionary(pair => pair.Key, pair => new
        {
            value = pair.Value.Value,
            scope = pair.Value.Scope,
            sourceFrameId = pair.Value.SourceFrameId,
            sampledAtFrameId = pair.Value.SampledAtFrameId,
            sessionId = pair.Value.SessionId
        }, StringComparer.Ordinal);
        var entry = new
        {
            actionIndex = action.Index,
            actionId = action.Id,
            name = action.Name,
            phase = "completedFrame",
            sessionId = ScenarioSessionId,
            scenarioTick = tick,
            sampledAtFrameId = completedFrameId,
            completedFrameId,
            worldFrame = headlessWorldFrame,
            observations = values
        };
        File.AppendAllText(Path.Combine(ScenarioOutputDirectory, "scenario-checkpoints.jsonl"),
            JsonSerializer.Serialize(entry) + Environment.NewLine);
    }

    private bool EvaluateScenarioAssertion(HeadlessScenarioAction action, int tick, ulong completedFrameId,
        IReadOnlyDictionary<string, ScenarioObservedField> fields)
    {
        ScenarioObservedField current = fields[action.Field!];
        JsonElement expected = action.Expected;
        bool passed;
        string? reason = null;
        if (action.Operator == "eq")
            passed = ScenarioValueEquals(current.Value, expected);
        else if (action.Operator == "gte")
            passed = TryObservedNumber(current.Value, out double actual) &&
                TryExpectedNumber(expected, out double threshold) && actual >= threshold;
        else
        {
            ScenarioCheckpoint baseline = scenarioCheckpoints[action.Baseline!];
            if (!StringComparer.Ordinal.Equals(baseline.SessionId, ScenarioSessionId) ||
                !baseline.Fields.TryGetValue(action.Field!, out ScenarioObservedField baselineField) ||
                !StringComparer.Ordinal.Equals(baselineField.SessionId, ScenarioSessionId))
            {
                passed = false;
                reason = "deltaGte baseline belongs to a different or missing session.";
            }
            else if (TryObservedNumber(current.Value, out double currentValue) &&
                     TryObservedNumber(baselineField.Value, out double baselineValue) &&
                     TryExpectedNumber(expected, out double delta))
                passed = currentValue - baselineValue >= delta;
            else
            {
                passed = false;
                reason = "deltaGte field is unavailable or not numeric.";
            }
        }
        if (!passed && reason == null) reason = "Assertion failed for field " + action.Field + ".";
        if (!scenarioExecutedActions.Add(action.Index))
        {
            FailHeadlessRun("Assertion action was executed more than once: " + action.Id, "completedFrame");
            return false;
        }
        WriteScenarioEvent(action, "completedFrame", true, true,
            new { passed, field = action.Field, observed = current.Value }, reason,
            completedFrameId: completedFrameId);
        if (!passed) FailHeadlessRun(reason!, "completedFrame");
        return passed;
    }

    private static bool ScenarioValueEquals(object? actual, JsonElement expected)
    {
        if (expected.ValueKind == JsonValueKind.Null) return actual == null;
        if (actual == null) return false;
        return expected.ValueKind switch
        {
            JsonValueKind.True or JsonValueKind.False => actual is bool value && value == expected.GetBoolean(),
            JsonValueKind.String => actual is string value &&
                StringComparer.Ordinal.Equals(value, expected.GetString()),
            JsonValueKind.Number => TryObservedNumber(actual, out double observed) &&
                TryExpectedNumber(expected, out double value) && observed == value,
            _ => false
        };
    }

    private static bool TryObservedNumber(object? value, out double number)
    {
        number = 0;
        if (value == null || value is bool or string) return false;
        try
        {
            number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return double.IsFinite(number);
        }
        catch (Exception error) when (error is FormatException or InvalidCastException or OverflowException)
        { return false; }
    }

    private static bool TryExpectedNumber(JsonElement value, out double number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number &&
            double.TryParse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
            double.IsFinite(number);
    }

    private (bool Hidden, bool Focused, bool StagedModLoaded, string ModLocation) CurrentScenarioInvariants()
    {
        string modLocation = HeadlessModDiscovery.LoadedModLocation();
        return (!Window.IsVisible, Window.IsFocused,
            HeadlessModDiscovery.IsStagedModLoaded(modLocation), modLocation);
    }

    private void RecordHeadlessRenderFailure(Exception error)
    {
        if (!HeadlessHarnessOptions.Active) return;
        try
        {
            if (HeadlessHarnessOptions.FrameDirectory is { } directory)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "render-failure.txt"), error.ToString());
            }
            FailHeadlessRun(error.GetBaseException().Message, scenarioFailurePhase);
        }
        catch { }
    }

    private void FailHeadlessRun(string reason, string failedPhase)
    {
        try
        {
            if (scenarioFailure != null) return;
            scenarioFailure = reason;
            headlessLegacyReady = false;
            headlessCompletionReason = null;
            scenarioFailurePhase = failedPhase;
            headlessDone = true;
            if (HeadlessHarnessOptions.Scenario != null)
            {
                try
                {
                    (bool hidden, bool focused, bool stagedModLoaded, string modLocation) = CurrentScenarioInvariants();
                    WriteScenarioTerminal(false, reason, failedPhase, lastSuccessfulScenarioFrameId,
                        hidden, focused, stagedModLoaded, diagnosticWorldReady, headlessLegacyReady);
                    WriteLegacyHeadlessResult(false, reason, hidden, focused, stagedModLoaded, modLocation);
                }
                catch { }
            }
            else
            {
                try
                {
                    (bool hidden, bool focused, bool stagedModLoaded, string modLocation) = CurrentScenarioInvariants();
                    WriteLegacyHeadlessResult(false, reason, hidden, focused, stagedModLoaded, modLocation);
                }
                catch { }
            }
            try { platform.Logger.Notification("VulkanStory headless: {0} frames; {1}", headlessWritten, reason); }
            catch { }
            try { Input.RequestWindowExit(); }
            catch { }
        }
        catch { }
    }

    private void FinalizeHeadlessRun(bool success, string reason, bool hidden, bool focused,
        bool stagedModLoaded, string modLocation, ulong? completedFrameId = null)
    {
        try
        {
            if (HeadlessHarnessOptions.Scenario != null)
                WriteScenarioTerminal(true, reason, "completedFrame", completedFrameId,
                    hidden, focused, stagedModLoaded, diagnosticWorldReady, headlessLegacyReady);
            WriteLegacyHeadlessResult(success, reason, hidden, focused, stagedModLoaded, modLocation);
        }
        catch (Exception error)
        {
            FailHeadlessRun("Terminal result write failed: " + error.Message, "completedFrame");
            return;
        }
        headlessDone = true;
        scenarioTerminal = HeadlessHarnessOptions.Scenario != null;
        try { platform.Logger.Notification("VulkanStory headless: {0} frames; {1}", headlessWritten, reason); }
        catch { }
        if (HeadlessHarnessOptions.ExitWhenDone) Input.RequestWindowExit();
    }

    private void WriteScenarioTerminal(bool success, string reason, string? failedPhase, ulong? completedFrameId,
        bool hidden, bool focused, bool stagedModLoaded, bool worldReady, bool legacyReady)
    {
        HeadlessScenario scenario = HeadlessHarnessOptions.Scenario!;
        var result = new
        {
            schema = 1,
            id = scenario.Id,
            context = scenario.Context,
            gameAssembly = typeof(GameRenderSession).Assembly.Location,
            sessionId = ScenarioSessionId,
            inputSha256 = scenario.InputSha256,
            expectedActionCount = scenario.Actions.Count,
            executedActionCount = scenarioExecutedActions.Count,
            requestedScenarioCaptures = scenario.Actions.Count(action => action.Kind == HeadlessScenarioActionKind.Capture),
            writtenScenarioCaptures = scenarioWrittenCaptures,
            pairedScenarioCaptures = scenarioPairedCaptures,
            legacyReady,
            complete = success,
            terminalPhase = success ? "completedFrame" : "failed",
            success,
            error = success ? null : reason,
            failedPhase = success ? null : failedPhase,
            finalCompletedFrameId = completedFrameId,
            hidden,
            focused,
            stagedModLoaded,
            worldReady
        };
        WriteJsonAtomically(Path.Combine(ScenarioOutputDirectory, "scenario-result.json"), result);
    }

    private void WriteLegacyHeadlessResult(bool success, string reason, bool hidden, bool focused,
        bool stagedModLoaded, string modLocation)
    {
        string? directory = HeadlessHarnessOptions.FrameDirectory;
        if (directory == null) return;
        Directory.CreateDirectory(directory);
        var result = new
        {
            success,
            context = HeadlessHarnessOptions.Scenario?.Context ?? (HeadlessHarnessOptions.MainMenuOptions ? "main" : "world"),
            gameAssembly = typeof(GameRenderSession).Assembly.Location,
            reason,
            hidden,
            focused,
            stagedModLoaded,
            modLocation,
            worldReady = diagnosticWorldReady,
            pid = Environment.ProcessId,
            worldFrame = headlessWorldFrame,
            requested = HeadlessHarnessOptions.Frames.Length,
            written = headlessWritten
        };
        WriteJsonAtomically(Path.Combine(directory, "headless-result.json"), result);
    }

    private static void WriteJsonAtomically(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value));
        File.Move(temporary, path, overwrite: true);
    }
}
