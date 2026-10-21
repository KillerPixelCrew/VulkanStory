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
    private bool scenarioFocusRequested;
    private string scenarioFailurePhase = "renderCycle";
    private string? scenarioFailure;
    private bool scenarioTerminal;
    private int scenarioWrittenCaptures, scenarioPairedCaptures;
    private ulong? lastSuccessfulScenarioFrameId;
    private string ScenarioSessionId => scenarioSessionId ??= Guid.NewGuid().ToString("N");
    private string ScenarioOutputDirectory => HeadlessHarnessOptions.FrameDirectory
        ?? throw new InvalidOperationException("Scenario output directory is not configured.");
    private static readonly bool NativeTaaInputCapture =
        Environment.GetEnvironmentVariable("VULKANSTORY_NATIVE_TAA_INPUTS") == "1";
    private static readonly bool QuadMotionInputCapture = Environment.GetEnvironmentVariable("VULKANSTORY_QUAD_MOTION_INPUTS") == "1";
    private static readonly bool SrInputCapture =
        Environment.GetEnvironmentVariable("VULKANSTORY_SR_INPUTS") == "1";
    private bool SrInputCaptureScheduled => SrInputCapture && HeadlessHarnessOptions.Enabled &&
        !scenarioTerminal && !headlessDone && scenarioTick.HasValue &&
        HeadlessHarnessOptions.Scenario is { } scenario && scenario.Actions.Any(action =>
            action.Tick == scenarioTick && action.Kind == HeadlessScenarioActionKind.Capture);

    // Capture at the actual consumer seam. Final composition overwrites Primary
    // later, so its ordinary post-frame dump cannot represent the TAA input.
    private void CaptureNativeTaaInputs()
    {
        CaptureQuadMotionInputs();
        if (!NativeTaaInputCapture || !HeadlessHarnessOptions.Enabled ||
            !services.RendererSettings.EffectiveTaa || scenarioTick == null ||
            HeadlessHarnessOptions.Scenario is not { } scenario || PrimaryTarget is not { } primary) return;
        foreach (var action in scenario.Actions.Where(a => a.Tick == scenarioTick && a.Kind == HeadlessScenarioActionKind.Capture))
        {
            string directory = Path.Combine(ScenarioOutputDirectory, "scenario-captures", action.Name!, "taa-input");
            Directory.CreateDirectory(directory);
            DumpNativeTaaTarget(directory, 0, "Primary", primary, true);
            int readIndex = Graphics.NextTaaReadIndex;
            if (platform.FrameBuffers.Count > readIndex && platform.FrameBuffers[readIndex] is { } read)
                DumpNativeTaaTarget(directory, readIndex, "TaaRead", read, false);
            DumpNativeTaaWindowProbes(Path.Combine(directory, "window"), Graphics.TaaSampleReadTargets);
        }
    }

    private void CaptureQuadMotionInputs()
    {
        if (!QuadMotionInputCapture || !HeadlessHarnessOptions.Enabled || scenarioTick == null || PrimaryTarget is not { } primary ||
            Graphics.FrameState.MotionAttachment < 0 || platform.FrameBuffers.Count <= GameGraphicsAdapter.QuadMotionIndex ||
            platform.FrameBuffers[GameGraphicsAdapter.QuadMotionIndex] is not { } layer || HeadlessHarnessOptions.Scenario is not { } scenario) return;
        var captures = scenario.Actions.Where(action => action.Tick == scenarioTick && action.Kind == HeadlessScenarioActionKind.Capture).ToArray();
        if (captures.Length == 0) return;
        var quad = Device.ReadTextureForParity(layer.ColorTextureIds[0]);
        var quadDepth = Device.ReadTextureForParity(layer.DepthTextureId);
        var motion = Device.ReadTextureForParity(primary.ColorTextureIds[Graphics.FrameState.MotionAttachment]);
        var depth = Device.ReadTextureForParity(primary.DepthTextureId);
        if (quad?.Floats is not { } vectors || quadDepth?.Floats is not { } sourceDepth || motion?.Floats is not { } scene || depth?.Floats is not { } sceneDepth)
            throw new InvalidOperationException("Quad particle motion readback is unavailable.");
        int pixels = quad.Width * quad.Height;
        if (quadDepth.Width != quad.Width || quadDepth.Height != quad.Height || motion.Width != quad.Width || motion.Height != quad.Height ||
            depth.Width != quad.Width || depth.Height != quad.Height ||
            vectors.Length < pixels * 4 || scene.Length < pixels * 4 || sourceDepth.Length < pixels || sceneDepth.Length < pixels)
            throw new InvalidOperationException("Quad particle motion readback size differs from the scene.");
        int sourceStride = sourceDepth.Length >= pixels * 4 ? 4 : 1, sceneStride = sceneDepth.Length >= pixels * 4 ? 4 : 1;
        var before = Graphics.QuadMotionCompositionInput;
        float[]? beforeDepth = before?.Depth.Floats;
        bool inclusionAvailable = before is { } input && input.FrameId == Device.LatencyFrameId &&
            input.Eye == Device.TerrainEyeIndex && input.Depth.Width == quad.Width && input.Depth.Height == quad.Height &&
            beforeDepth != null && beforeDepth.Length >= pixels;
        int beforeStride = beforeDepth?.Length >= pixels * 4 ? 4 : 1;
        long writers = 0, compared = 0, matched = 0, mismatched = 0;
        long shaderRejected = 0, depthRejected = 0, equalDepthRejected = 0;
        long eligible = 0, eligibleMatched = 0, eligibleMismatched = 0, eligibleDepthMismatched = 0;
        double maximumError = 0, eligibleMaximumError = 0;
        int maximumErrorIndex = -1, eligibleMaximumErrorIndex = -1;
        string maximumErrorInclusion = "unknown";
        var mismatchPixels = new List<object>();
        object Pixel(int i, string inclusion, double error) => new
        {
            x = i % quad.Width, y = i / quad.Width, inclusion, error,
            quad = vectors.AsSpan(i * 4, 4).ToArray(), quadDepth = sourceDepth[i * sourceStride],
            sceneDepthBeforeCompose = inclusionAvailable ? (float?)beforeDepth![i * beforeStride] : null,
            scene = scene.AsSpan(i * 4, 4).ToArray(), sceneDepth = sceneDepth[i * sceneStride]
        };
        for (int i = 0; i < pixels; i++)
        {
            int at = i * 4;
            if (vectors[at + 2] <= 0 || vectors[at + 3] <= 0) continue;
            writers++;
            float actualDepth = sourceDepth[i * sourceStride];
            double error = Math.Max(Math.Abs((double)vectors[at] - scene[at]), Math.Abs((double)vectors[at + 1] - scene[at + 1]));
            if (!double.IsFinite(error)) error = double.MaxValue;
            bool depthMatches = !(sceneDepth[i * sceneStride] != actualDepth ||
                Math.Abs(scene[at + 3] - actualDepth) > Math.Max(0.0002f, 0.0008f * actualDepth));
            // Match particle-motion-compose.fsh's discard and the pipeline's
            // strict LESS against its input depth, not its overwritten output.
            bool shaderAccepts = !(vectors[at + 2] <= 0 || vectors[at + 3] <= 0 || actualDepth >= 1 ||
                MathF.Abs(vectors[at + 3] - actualDepth) > MathF.Max(0.0002f, 0.0008f * actualDepth));
            bool included = inclusionAvailable && shaderAccepts && actualDepth < beforeDepth![i * beforeStride];
            string inclusion = !inclusionAvailable ? "unknown" : !shaderAccepts ? "shader-discard" : included ? "eligible" :
                actualDepth == beforeDepth![i * beforeStride] ? "equal-depth-rejected" : "depth-rejected";
            // Preserve the original population and maximum for r24 comparison.
            if (depthMatches)
            {
                compared++;
                if (error > maximumError) { maximumError = error; maximumErrorIndex = i; maximumErrorInclusion = inclusion; }
                if (error <= 0.01) matched++; else mismatched++;
            }
            if (inclusionAvailable)
            {
                if (!shaderAccepts) shaderRejected++;
                else if (!included)
                {
                    depthRejected++;
                    if (actualDepth == beforeDepth![i * beforeStride]) equalDepthRejected++;
                }
                else
                {
                    eligible++;
                    if (error > eligibleMaximumError) { eligibleMaximumError = error; eligibleMaximumErrorIndex = i; }
                    if (!depthMatches) eligibleDepthMismatched++;
                    // Composition copies stored RG values without arithmetic;
                    // any difference on a required write is unexpected.
                    if (depthMatches && error == 0) eligibleMatched++; else eligibleMismatched++;
                }
            }
            if (mismatchPixels.Count < 16 && ((depthMatches && error > 0.01) || (included && (!depthMatches || error > 0))))
                mismatchPixels.Add(Pixel(i, inclusion, error));
        }
        object? maximumErrorPixel = maximumErrorIndex >= 0 ? Pixel(maximumErrorIndex, maximumErrorInclusion, maximumError) : null;
        object? eligibleMaximumErrorPixel = eligibleMaximumErrorIndex >= 0 ? Pixel(eligibleMaximumErrorIndex, "eligible", eligibleMaximumError) : null;
        foreach (var action in captures)
        {
            string directory = Path.Combine(ScenarioOutputDirectory, "scenario-captures", action.Name!);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "quad-motion.json"), JsonSerializer.Serialize(new
            {
                frameId = Device.LatencyFrameId, eye = Device.TerrainEyeIndex,
                writers, compared, matched, mismatched, maximumError, maximumErrorPixel,
                inclusionAvailable, compositionInputFrameId = before?.FrameId, compositionInputEye = before?.Eye,
                compositionDrawn = Graphics.QuadMotionCompositionCaptureDrawn,
                shaderRejected, depthRejected, equalDepthRejected,
                eligible, eligibleMatched, eligibleMismatched, eligibleDepthMismatched, eligibleMaximumError, eligibleMaximumErrorPixel,
                mismatchPixels, width = quad.Width, height = quad.Height,
                meaning = "Original depth-equality/0.01 comparison retained; eligible uses compose shader predicates and pre-compose strict LESS, requiring exact RG copy and matching depth. Pixel y is GL row order."
            }, new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        }
    }

    // SR owns a separate consumer seam. Capture after its dispatch has completed
    // recording, while Primary still holds the scene and before the post tail.
    private void CaptureSrInputs()
    {
        if (!SrInputCaptureScheduled || !Graphics.UpscaledThisFrame || PrimaryTarget is not { } primary ||
            platform.FrameBuffers.Count <= GameGraphicsAdapter.UpscaledSceneIndex ||
            platform.FrameBuffers[GameGraphicsAdapter.UpscaledSceneIndex] is not { } output) return;
        GameTemporalFrame temporal = Temporal.Snapshot();
        ulong frameId = Device.LatencyFrameId;
        bool currentWorldSample = temporal.WorldCaptured && temporal.FrameId == frameId;
        var sdkInputs = Device.LastUpscalerInputTextures;
        var xessInputs = Device.LastXessInputTextures;
        bool currentSdkInputs = currentWorldSample && sdkInputs.FrameId == frameId;
        foreach (var action in HeadlessHarnessOptions.Scenario!.Actions.Where(a =>
                     a.Tick == scenarioTick && a.Kind == HeadlessScenarioActionKind.Capture))
        {
            string captureDirectory = Path.Combine(ScenarioOutputDirectory, "scenario-captures", action.Name!);
            string inputDirectory = Path.Combine(captureDirectory, "sr-input");
            string outputDirectory = Path.Combine(captureDirectory, "sr-output");
            Directory.CreateDirectory(inputDirectory);
            Directory.CreateDirectory(outputDirectory);
            DumpNativeTaaTarget(inputDirectory, 0, "Primary", primary, true);
            if (Graphics.UsesFsr3Inputs && Graphics.Fsr3MaterialMotionCaptureFrameId == frameId &&
                Graphics.Fsr3MaterialMotionCapture is { } materialMotion)
                HeadlessParityDump.Write(inputDirectory, 63, "MaterialMotion", "color0", materialMotion);
            if (Graphics.GodRaysInScene && platform.FrameBuffers[(int)EnumFrameBuffer.GodRays] is { } rays)
                DumpNativeTaaTarget(inputDirectory, (int)EnumFrameBuffer.GodRays, "SceneGodRays", rays, false);
            if (platform.FrameBuffers[(int)EnumFrameBuffer.Transparent] is { } transparent)
            {
                // OIT replaces attachment zero with a private reveal texture.
                // Slots one and two remain the exact revealage/glow SR consumed.
                for (int slot = 1; slot < Math.Min(3, transparent.ColorTextureIds.Length); slot++)
                    if (Device.ReadTextureForParity(transparent.ColorTextureIds[slot]) is { } data)
                        HeadlessParityDump.Write(inputDirectory, (int)EnumFrameBuffer.Transparent,
                            "Transparent", "color" + slot, data);
            }
            if (currentSdkInputs)
            {
                if (xessInputs.FrameId == frameId)
                {
                    if (Device.ReadTextureForParity(xessInputs.Color) is { } sdkColor)
                        HeadlessParityDump.Write(inputDirectory, 64, "SdkColor", "color0", sdkColor);
                    if (Device.ReadTextureForParity(xessInputs.Depth) is { } sdkDepth)
                        HeadlessParityDump.Write(inputDirectory, 65, "SdkDepth", "color0", sdkDepth);
                    if (Device.ReadTextureForParity(xessInputs.Output) is { } sdkOutput)
                        HeadlessParityDump.Write(outputDirectory, 66, "SdkUprightOutput", "color0", sdkOutput);
                }
                if (sdkInputs.Motion > 0 && Device.ReadTextureForParity(sdkInputs.Motion) is { } motion)
                    HeadlessParityDump.Write(inputDirectory, 60, "SdkMotion", "color0", motion);
                if (sdkInputs.Reactive > 0 && Device.ReadTextureForParity(sdkInputs.Reactive) is { } reactive)
                    HeadlessParityDump.Write(inputDirectory, 61, "SdkReactive", "color0", reactive);
                if (services.RendererSettings.EffectiveUpscaler == "fsr3" &&
                    Device.Fsr3CompositionTexture > 0 && Device.ReadTextureForParity(Device.Fsr3CompositionTexture) is { } composition)
                    HeadlessParityDump.Write(inputDirectory, 62, "SdkComposition", "color0", composition);
            }
            DumpNativeTaaTarget(outputDirectory, GameGraphicsAdapter.UpscaledSceneIndex, "SdkOutput", output, false);
            WriteScenarioFrameInputs(inputDirectory, frameId, temporal, currentWorldSample,
                output.Width, output.Height, "postUpscalerPrePostTail");
        }
    }

    private void DumpNativeTaaTarget(string directory, int slot, string name, FrameBufferRef target, bool primary)
    {
        for (int i = 0; i < target.ColorTextureIds.Length; i++)
        {
            if (primary && i > 1 && i != Graphics.FrameState.MotionAttachment) continue;
            if (Device.ReadTextureForParity(target.ColorTextureIds[i]) is { } data)
                HeadlessParityDump.Write(directory, slot, name, "color" + i, data);
        }
        if (primary && target.DepthTextureId > 0 && Device.ReadTextureForParity(target.DepthTextureId) is { } depth)
            HeadlessParityDump.Write(directory, slot, name, "depth", depth);
    }

    // Cache probes retain the exact local footprint used by the failing scene
    // pixels without exporting thirty full-size auxiliary images per capture.
    private void DumpNativeTaaWindowProbes(string directory, FrameBufferRef[] targets)
    {
        foreach (FrameBufferRef target in targets)
        for (int attachment = 0; attachment < target.ColorTextureIds.Length; attachment++)
        {
            foreach (var focus in new[] { (Name: "treeline", X: 2139, Y: 396), (Name: "pine", X: 1619, Y: 358) })
            {
                int width = Math.Min(64, target.Width), height = Math.Min(64, target.Height);
                int x = Math.Clamp((int)((long)focus.X * target.Width / 2560) - width / 2, 0, target.Width - width);
                int pngTop = Math.Clamp((int)((long)focus.Y * target.Height / 1528) - height / 2, 0, target.Height - height);
                int y = target.Height - pngTop - height;
                if (Device.ReadTextureForParity(target.ColorTextureIds[attachment], x, y, width, height) is not { } crop) continue;
                string probeDirectory = Path.Combine(directory, focus.Name);
                Directory.CreateDirectory(probeDirectory);
                File.WriteAllText(Path.Combine(probeDirectory, "region.json"), JsonSerializer.Serialize(new
                    { fullWidth = target.Width, fullHeight = target.Height, x, y, pngTop, width, height }));
                HeadlessParityDump.Write(probeDirectory, target.FboId, "TaaSamples", "color" + attachment, crop);
            }
        }
    }

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
        Graphics.PrepareQuadMotionInputCapture(false);
        if (HeadlessTerrainComparison.Pending) { scenarioTerrainFrameHeld = true; return; }
        if (scenarioTerrainFrameHeld) { scenarioTerrainFrameHeld = false; return; }
        HeadlessScenario? scenario = HeadlessHarnessOptions.Scenario;
        if (scenario == null || scenarioTerminal || headlessDone) return;
        if (ScenarioWorldCyclePending)
        {
            try { AdvanceScenarioWorldCycle(scenario); }
            catch (Exception error)
            {
                try { WriteScenarioEvent(scenarioWorldCycle!.Action, "beforeInput", false, false, null,
                    error.GetBaseException().Message); }
                catch { }
                FailHeadlessRun("World cycle failed: " + error.GetBaseException().Message, "beforeInput");
            }
            // A verified reload restores the declared tick for this completed frame.
            // The next ordinary input boundary advances it exactly once.
            return;
        }
        if (scenarioTick == null)
        {
            try
            {
                if (HeadlessHarnessOptions.RequireInitialFocus)
                {
                    if (!Window.IsVisible) return;
                    if (!scenarioFocusRequested)
                    {
                        scenarioFocusRequested = true;
                        try { Window.Focus(); }
                        catch (InvalidOperationException error)
                        { Console.Error.WriteLine("[VulkanStory] Focused scenario could not raise its window: " + error.Message); }
                        Console.Error.WriteLine("[VulkanStory] Focused scenario requested window focus once; waiting for confirmation.");
                    }
                    if (!Window.IsFocused) return;
                }
                if (!TryStartScenarioContext(scenario)) return;
            }
            catch (Exception error) { FailHeadlessRun(error.GetBaseException().Message, "beforeInput"); return; }
            scenarioTick = 0;
        }
        else scenarioTick++;

        Graphics.PrepareQuadMotionInputCapture(QuadMotionInputCapture && HeadlessHarnessOptions.Enabled &&
            scenario.Actions.Any(action => action.Tick == scenarioTick && action.Kind == HeadlessScenarioActionKind.Capture));

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
                         HeadlessScenarioActionKind.Options or HeadlessScenarioActionKind.Camera or
                         HeadlessScenarioActionKind.Resize or HeadlessScenarioActionKind.WorldCycle))
        {
            string phase = ScenarioActionPhase(action.Kind);
            scenarioFailurePhase = phase;
            try
            {
                if (action.Kind == HeadlessScenarioActionKind.WorldCycle)
                {
                    StartScenarioWorldCycle(action);
                    return;
                }
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
                else if (action.Kind == HeadlessScenarioActionKind.Camera)
                {
                    var client = Temporal.CurrentClient ?? throw new InvalidOperationException("Camera action requires a world client.");
                    if (client.IsPaused) throw new InvalidOperationException("Camera action requires an unpaused world.");
                    if (action.CameraMode is { } modeName)
                    {
                        var mode = Enum.Parse<EnumCameraMode>(modeName);
                        for (int attempt = 0; attempt < 2 && client.api.Render.CameraType != mode; attempt++)
                            client.MainCamera.CycleMode();
                        if (client.api.Render.CameraType != mode) throw new InvalidOperationException("Camera mode switch failed.");
                        Temporal.State.RequestReset(EnumTemporalResetReason.Toggle);
                    }
                    // Official PlayerCamera reads mouseYaw in the Before stage.
                    // Move that real camera input before it builds this frame's matrices.
                    client.mouseYaw = Vintagestory.API.MathTools.GameMath.Mod(
                        client.mouseYaw + action.YawDeltaDegrees * MathF.PI / 180f, MathF.Tau);
                    client.EntityPlayer.Pos.Yaw = client.mouseYaw;
                }
                // Drawable pixels, the unit of captures and platform.WindowSize; SDL events own resize and GUI recomposition.
                else Window.SetPixelSize(action.Width, action.Height);
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
            actionKind = action.Kind == HeadlessScenarioActionKind.WorldCycle
                ? "world-cycle" : action.Kind.ToString().ToLowerInvariant(),
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
        HeadlessScenarioActionKind.Options or HeadlessScenarioActionKind.Camera or HeadlessScenarioActionKind.Resize or
            HeadlessScenarioActionKind.WorldCycle => "beforeInput",
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
        if (HeadlessTerrainComparison.Pending) { scenarioTerrainFrameHeld = true; return; }
        if (scenarioTerrainFrameHeld) return;
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
        if (NativeTaaInputCapture && Graphics.TaaResolvedThisFrame)
        {
            WriteScenarioFrameInputs(captureDirectory, captureFrameId, temporal, currentWorldSample, size.Width, size.Height);
            int writeIndex = Graphics.ResolvedTaaWriteIndex;
            if (platform.FrameBuffers.Count > writeIndex && platform.FrameBuffers[writeIndex] is { } write)
                DumpNativeTaaTarget(Path.Combine(captureDirectory, "taa-output"), writeIndex, "TaaWrite", write, false);
            DumpNativeTaaWindowProbes(Path.Combine(captureDirectory, "taa-output", "window"), Graphics.TaaSampleWrittenTargets);
        }

        var files = Directory.EnumerateFiles(captureDirectory, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path =>
            {
                dimensions.TryGetValue(Path.GetFullPath(path), out var fileSize);
                using var file = File.OpenRead(path);
                return new ScenarioFile(
                    Path.GetRelativePath(ScenarioOutputDirectory, path).Replace('\\', '/'),
                    Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(),
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
        in GameTemporalFrame raw, bool currentWorldSample, int width, int height,
        string phase = "preGenerateReadback")
    {
        object? temporalFrameId = currentWorldSample ? raw.FrameId : null;
        FrameBufferRef? primary = PrimaryTarget;
        var sdkInputs = Device.LastUpscalerInputTextures;
        var xessInputs = Device.LastXessInputTextures;
        bool currentXessInputs = currentWorldSample && services.RendererSettings.EffectiveUpscaler == "xess" &&
            xessInputs.FrameId == raw.FrameId;
        object? srInputs = SrInputCapture && currentWorldSample && Graphics.UpscaledThisFrame &&
            sdkInputs.FrameId == raw.FrameId ? new
            {
                frameId = sdkInputs.FrameId,
                provider = services.RendererSettings.EffectiveUpscaler,
                quality = services.RendererSettings.Quality,
                renderWidth = primary?.Width,
                renderHeight = primary?.Height,
                displayWidth = width,
                displayHeight = height,
                jitterX = raw.Provider.JitterX,
                jitterY = raw.Provider.JitterY,
                inputOrientation = currentXessInputs ? "upright" : "renderer",
                colorTexture = currentXessInputs ? xessInputs.Color : primary?.ColorTextureIds.ElementAtOrDefault(0),
                depthTexture = currentXessInputs ? xessInputs.Depth : primary?.DepthTextureId,
                sourceMotionTexture = Graphics.PreparedSdkMotionTexture,
                rawSceneMotionTexture = primary?.ColorTextureIds.ElementAtOrDefault(Graphics.FrameState.MotionAttachment),
                motionTexture = sdkInputs.Motion,
                reactiveTexture = sdkInputs.Reactive,
            } : null;
        File.WriteAllText(Path.Combine(directory, "frame-inputs.json"), JsonSerializer.Serialize(new
        {
            phase,
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
            upscalerQuality = currentWorldSample ? services.RendererSettings.Quality : null,
            upscaleEvaluated = currentWorldSample ? Graphics.UpscaledThisFrame : (bool?)null,
            godRaysInScene = currentWorldSample ? Graphics.GodRaysInScene : (bool?)null,
            fsr3MaterialCaptureFrameId = currentWorldSample && Graphics.UsesFsr3Inputs ? (ulong?)Device.Fsr3MaterialReactiveFrameId : null,
            fsr3HasComposition = currentWorldSample && Graphics.UsesFsr3Inputs ? (bool?)Graphics.Fsr3HasComposition : null,
            xessConstants = currentWorldSample ? Device.XessConstantsCapture : null,
            srInputs,
            skyMotion = currentWorldSample ? Graphics.SkyMotionCapture : null,
            preparedSdkMotionTexture = currentWorldSample ? (int?)Graphics.PreparedSdkMotionTexture : null,
            sdkMotion = currentWorldSample ? Graphics.SdkMotionCapture : null,
            nativeTaaResolve = currentWorldSample && Graphics.TaaResolvedThisFrame ? Graphics.TaaResolveCapture : null,
            resourceMemory = Device.ResourceMemoryDiagnostics(),
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
        if (HeadlessTerrainComparison.Failure is { } terrainError)
        { FailHeadlessRun(terrainError, "terrainProbe"); return; }
        if (HeadlessTerrainComparison.Pending) { scenarioTerrainFrameHeld = true; return; }
        if (scenarioTerrainFrameHeld) return;
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
        FrameBufferRef? primary = PrimaryTarget;
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
        Current("upscaleEvaluatedThisFrame", Graphics.UpscaledThisFrame);
        Current("taaResolvedThisFrame", Graphics.TaaResolvedThisFrame);
        Current("godRaysInScene", Graphics.GodRaysInScene);
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
        Aggregate("sceneDrawCommandsTerrain", Device.SceneDrawCommandsTerrain);
        Aggregate("sceneDrawCommandsEntity", Device.SceneDrawCommandsEntity);
        Aggregate("sceneDrawCommandsParticle", Device.SceneDrawCommandsParticle);
        Aggregate("sceneDrawCommandsCompat", Device.SceneDrawCommandsCompat);
        Aggregate("mapPageRendererActive", Performance.WorldMapPageCache.HasActiveRenderer);
        Aggregate("managedDependencyResolutionVerified", ModCompatibilityConsumerPatches.ManagedDependencyResolutionVerified);
        Aggregate("validAnimatedHistoryUploads", Temporal.EntityMotion.ValidAnimationUploads);
        Aggregate("deformationDispatchesRecorded", Device.DeformationDispatchesRecorded);
        Aggregate("worldGeometryDispatchesRecorded", Device.WorldGeometryDispatchesRecorded);
        Aggregate("worldGeometryCacheReuses", Device.WorldGeometryCacheReuses);
        Aggregate("particleGeometryDispatchesRecorded", Device.ParticleGeometryDispatchesRecorded);
        Aggregate("quadGeometryDispatchesRecorded", Device.QuadGeometryDispatchesRecorded);
        Aggregate("quadMotionDrawsRecorded", Graphics.QuadMotionDrawsRecorded);
        Aggregate("quadMotionCompositesRecorded", Graphics.QuadMotionCompositesRecorded);
        Aggregate("quadMotionValidInstances", Graphics.QuadMotionValidInstances);
        Aggregate("particleGeometryCacheReuses", Device.ParticleGeometryCacheReuses);
        Aggregate("particleVisualDispatchesRecorded", Device.ParticleVisualDispatchesRecorded);
        Aggregate("particleVisualChecks", Device.ParticleVisualChecks);
        Aggregate("particleVisualChecksPassed", Device.ParticleVisualChecksPassed);
        Aggregate("particleVisualMaximumError", Device.ParticleVisualMaximumError);
        Aggregate("particleVisualMaximumColorError", Device.ParticleVisualMaximumColorError);
        Aggregate("particleVisualFunctionCoverage", Device.ParticleVisualFunctionCoverage);
        Aggregate("cpuParticlePropertiesDeferred", Performance.GpuParticleVisuals.DeferredProperties);
        Aggregate("cpuParticleFacadeChecks", Performance.GpuParticleVisuals.CpuFacadeChecks);
        Aggregate("cpuParticleFacadeValid", Performance.GpuParticleVisuals.CpuFacadeValid);
        Aggregate("terrainNoiseAvailable", Device.TerrainNoiseAvailable);
        Aggregate("terrainNoiseDispatchesRecorded", Device.TerrainNoiseDispatchesRecorded);
        Aggregate("terrainNoiseAppliedSamples", Performance.GpuTerrainNoise.AppliedSamples);
        Aggregate("terrainDensityAvailable", Device.TerrainDensityAvailable);
        Aggregate("terrainDensityAvailabilityReason", Device.TerrainDensityAvailabilityReason);
        Aggregate("terrainDensityDispatchesRecorded", Device.TerrainDensityDispatchesRecorded);
        Aggregate("terrainGpuPartsTested", Device.TerrainGpuPartsTested);
        Aggregate("terrainGpuPartsVisible", Device.TerrainGpuPartsVisible);
        Aggregate("terrainParityChecks", Device.TerrainParityChecks);
        Aggregate("terrainParityMismatches", Device.TerrainParityMismatches);
        Aggregate("chunkMeshingAvailable", Device.ChunkMeshingAvailable);
        Aggregate("chunkMeshingJobsCompleted", Device.ChunkMeshingJobsCompleted);
        Aggregate("chunkMeshingEqualityChecks", Performance.GpuChunkMeshing.EqualityChecks);
        Aggregate("chunkMeshingEqualityMismatches", Performance.GpuChunkMeshing.EqualityMismatches);
        Aggregate("chunkMeshingFacesPublished", Device.ChunkMeshingFacesPublished);
        Aggregate("chunkMeshingPayloadsDetached", Device.ChunkMeshingPayloadsDetached);
        Current("chunkMeshingPayloadStorageBytes", Device.ChunkMeshingPayloadStorageBytes);
        Aggregate("chunkMeshingPayloadsSuperseded", Performance.GpuChunkMeshing.PayloadsSuperseded);
        Aggregate("chunkMeshingStalePublicationsDropped", Performance.GpuChunkMeshing.StalePublicationsDropped);
        Aggregate("chunkMeshingRequeuedDuringFlight", Performance.GpuChunkMeshing.RequeuedDuringFlight);
        foreach (var counter in Performance.GpuChunkMeshing.AdmissionCounters()) Aggregate(counter.Key, counter.Value);
        Aggregate("chunkMeshingGenerationWaitMilliseconds", Performance.GpuChunkMeshing.GenerationWaitMilliseconds);
        Current("chunkMeshingPendingGenerations", Performance.GpuChunkMeshing.PendingGenerations);
        var undrawn = Performance.GpuChunkMeshing.UndrawnState(Temporal.CurrentClient);
        Current("chunkMeshingUndrawnChunks", undrawn?.Chunks);
        Current("chunkMeshingUndrawnEntities", undrawn?.Entities);
        Current("terrainComparisonComplete", HeadlessTerrainComparison.Complete);
        Current("terrainComparisonOutputEqual", HeadlessTerrainComparison.Equal);
        Current("terrainComparisonGpuEquivalent", HeadlessTerrainComparison.GpuEquivalent);
        Current("terrainComparisonWrittenColumns", HeadlessTerrainComparison.Written);
        Aggregate("particleSimulationAvailable", Device.ParticleSimulationAvailable);
        Aggregate("particleSimulationSeparateQueue", Device.ParticleSimulationSeparateQueue);
        Aggregate("particleSimulationDispatchesRecorded", Device.ParticleSimulationDispatchesRecorded);
        Aggregate("particleSimulationStepsRecorded", Device.ParticleSimulationStepsRecorded);
        Aggregate("particleSimulationStepsApplied", Performance.GpuParticleSimulation.StepsApplied);
        Aggregate("particleSimulationCenterDispatchesRecorded", Device.ParticleSimulationCenterDispatchesRecorded);
        Aggregate("particleSimulationCentersRecorded", Device.ParticleSimulationCentersRecorded);
        Aggregate("particleInstanceDispatchesRecorded", Device.ParticleInstanceDispatchesRecorded);
        Aggregate("particleInstancesRecorded", Device.ParticleInstancesRecorded);
        Aggregate("particleInstanceHistoriesRecorded", Device.ParticleInstanceHistoriesRecorded);
        Aggregate("particleSimulationRetainedBytes", Device.ParticleSimulationRetainedBytes);
        Aggregate("particleSimulationCapacityFallbacks", Device.ParticleSimulationCapacityFallbacks);
        Aggregate("particleSimulationChecks", Performance.GpuParticleSimulation.Checks);
        Aggregate("particleSimulationChecksPassed", Performance.GpuParticleSimulation.ChecksPassed);
        Aggregate("particleLiquidPolicyChecks", Performance.GpuParticleSimulation.LiquidPolicyChecks);
        Aggregate("particleLiquidAirChecks", Performance.GpuParticleSimulation.AirPolicyChecks);
        Aggregate("particleLiquidDeathChecks", Performance.GpuParticleSimulation.LiquidDeathPolicyChecks);
        Aggregate("particleLiquidWetChecks", Performance.GpuParticleSimulation.WetPolicyChecks);
        Aggregate("particleLiquidPolicyApplied", Performance.GpuParticleSimulation.LiquidPolicyApplied);
        Aggregate("particleLiquidAirApplied", Performance.GpuParticleSimulation.AirPolicyApplied);
        Aggregate("particleLiquidDeathApplied", Performance.GpuParticleSimulation.LiquidDeathPolicyApplied);
        Aggregate("particleLiquidWetApplied", Performance.GpuParticleSimulation.WetPolicyApplied);
        Aggregate("particleLightingGpuSteps", Performance.GpuParticleSimulation.GpuLightingSteps);
        Aggregate("particleLightingCpuFallbackSteps", Performance.GpuParticleSimulation.CpuLightingSteps);
        Aggregate("particleDirectionGpuSteps", Performance.GpuParticleSimulation.GpuDirectionSteps);
        Aggregate("particleDirectionCpuFallbackSteps", Performance.GpuParticleSimulation.CpuDirectionSteps);
        Current("particleLiquidPolicyCoveragePassed", HeadlessParticleEvolution.LiquidPolicyCoveragePassed);
        Current("particleLiquidWetAvailable", HeadlessParticleEvolution.LiquidPolicyFixtureWetExpected);
        Aggregate("particleSimulationFloatError", Performance.GpuParticleSimulation.MaximumFloatError);
        Aggregate("particleSimulationPositionError", Performance.GpuParticleSimulation.MaximumPositionError);
        Aggregate("particleSimulationSubmitMilliseconds", Device.ParticleSimulationSubmitMilliseconds);
        Aggregate("particleSimulationGpuMilliseconds", Device.ParticleSimulationGpuMilliseconds);
        Aggregate("particleSimulationCpuBenchmarkSteps", Performance.GpuParticleSimulation.CpuBenchmarkSteps);
        Aggregate("particleSimulationGpuBenchmarkSteps", Performance.GpuParticleSimulation.GpuBenchmarkSteps);
        Aggregate("particleSimulationFallbackBenchmarkSteps", Performance.GpuParticleSimulation.FallbackBenchmarkSteps);
        Aggregate("particleSimulationCpuBenchmarkMilliseconds", Performance.GpuParticleSimulation.CpuBenchmarkMilliseconds);
        Aggregate("particleSimulationGpuBenchmarkMilliseconds", Performance.GpuParticleSimulation.GpuBenchmarkMilliseconds);
        Aggregate("particleSimulationFallbackBenchmarkMilliseconds", Performance.GpuParticleSimulation.FallbackBenchmarkMilliseconds);
        Aggregate("particleSimulationCpuBenchmarkPools", Performance.GpuParticleSimulation.CpuBenchmarkPools);
        Aggregate("particleSimulationGpuBenchmarkPools", Performance.GpuParticleSimulation.GpuBenchmarkPools);
        Aggregate("particleSimulationCpuBenchmarkPoolMilliseconds", Performance.GpuParticleSimulation.CpuBenchmarkPoolMilliseconds);
        Aggregate("particleSimulationGpuBenchmarkPoolMilliseconds", Performance.GpuParticleSimulation.GpuBenchmarkPoolMilliseconds);
        Aggregate("jointPoseDispatchesRecorded", Device.JointPoseDispatchesRecorded);
        Aggregate("jointPoseCacheReuses", Device.JointPoseCacheReuses);
        Aggregate("jointPoseChecks", Device.JointPoseChecks);
        Aggregate("jointPoseChecksPassed", Device.JointPoseChecksPassed);
        Aggregate("jointPoseMaximumRelativeError", Device.JointPoseMaximumRelativeError);
        Aggregate("cpuJointProductsDeferred", Performance.GpuJointPoses.DeferredJointProducts);
        Aggregate("cpuJointFacadeChecks", Performance.GpuJointPoses.CpuFacadeChecks);
        Aggregate("cpuJointFacadeValid", Performance.GpuJointPoses.CpuFacadeValid);
        Aggregate("deformationCacheReuses", Device.DeformationCacheReuses);
        Aggregate("entityVisibilityDispatchesRecorded", Device.EntityVisibilityDispatchesRecorded);
        Aggregate("entityIndirectDrawsRecorded", Device.EntityIndirectDrawsRecorded);
        Aggregate("entityCombinedDrawsRecorded", Device.EntityCombinedDrawsRecorded);
        Aggregate("entityGeometryPacksRecorded", Device.EntityGeometryPacksRecorded);
        Aggregate("entityPackedDrawsRecorded", Device.EntityPackedDrawsRecorded);
        Aggregate("entityRecordDrawsRecorded", Device.EntityRecordDrawsRecorded);
        Aggregate("entityRecordRefusals", Device.EntityRecordRefusals);
        Aggregate("entityResidentRowsCaptured", Device.EntityResidentRowsCaptured);
        Aggregate("entityResidentCaptureRefusals", Device.EntityResidentCaptureRefusals);
        Aggregate("entityRunsOpened", Device.EntityRunsOpened);
        Aggregate("entityRunBreaks", Device.EntityRunBreaks);
        Aggregate("entityRunBreakContiguity", Device.EntityRunBreakContiguity);
        Aggregate("entityRunBreakOrder", Device.EntityRunBreakOrder);
        Aggregate("entityRunBreakScope", Device.EntityRunBreakScope);
        Aggregate("entityRunBreakDynamicState", Device.EntityRunBreakDynamicState);
        Aggregate("entityRunBreakFrameSet", Device.EntityRunBreakFrameSet);
        Aggregate("entityRunBreakProgram", Device.EntityRunBreakProgram);
        Aggregate("entityRunBreakReplaced", Device.EntityRunBreakReplaced);
        Aggregate("entityAnimatedDrawsRecorded", Device.EntityAnimatedDrawsRecorded);
        Aggregate("entityAnimatedProducerDraws", Device.EntityAnimatedProducerDraws);
        Aggregate("entityAnimatedWorldOnlyProducerDraws", Device.EntityAnimatedWorldOnlyProducerDraws);
        Aggregate("entityAnimatedHoistedProducerDraws", Device.EntityAnimatedHoistedProducerDraws);
        Aggregate("entityAnimatedCombinedDrawsRecorded", Device.EntityAnimatedCombinedDrawsRecorded);
        Aggregate("animatedMotionWindows", Graphics.AnimatedMotionWindows);
        Aggregate("validRigidHistoryWrites", Temporal.StandardMotion.ValidHistoryWrites);
        Aggregate("rigidEntityMotionWindows", Graphics.RigidEntityMotionWindows);
        Aggregate("validRigidEntityHistoryWrites", Graphics.ValidRigidEntityHistoryWrites);
        Aggregate("inventoryMotionWindows", Graphics.InventoryMotionWindows);
        Aggregate("validInventoryHistoryWrites", Graphics.ValidInventoryHistoryWrites);
        Aggregate("inventorySharedGeometryDraws", Graphics.InventorySharedGeometryDraws);
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
        HeadlessTerrainComparison.Cancel(reason);
        ClearScenarioWorldCycle();
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
        ClearScenarioWorldCycle();
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
            worldReady,
            terrainComparisonComplete = HeadlessTerrainComparison.Complete,
            terrainComparisonOutputEqual = HeadlessTerrainComparison.Equal,
            terrainComparisonGpuEquivalent = HeadlessTerrainComparison.GpuEquivalent,
            terrainComparisonWrittenColumns = HeadlessTerrainComparison.Written
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
            context = HeadlessHarnessOptions.Scenario?.Context ?? (HeadlessHarnessOptions.MainMenuContext ? "main" : "world"),
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
            written = headlessWritten,
            terrainComparisonComplete = HeadlessTerrainComparison.Complete,
            terrainComparisonOutputEqual = HeadlessTerrainComparison.Equal,
            terrainComparisonGpuEquivalent = HeadlessTerrainComparison.GpuEquivalent,
            terrainComparisonWrittenColumns = HeadlessTerrainComparison.Written
        };
        WriteJsonAtomically(Path.Combine(directory, "headless-result.json"), result);
    }

    private static void WriteJsonAtomically(string path, object value) =>
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(value));
}
