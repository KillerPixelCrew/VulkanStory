using System;
using System.IO;
using System.Text.RegularExpressions;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// The latency seams in source (Vulkan-native plan, section "Latency seams"): where
/// the sleep, the markers, the submit tags, the swapchain callbacks and the backend
/// selection sit, and what decides which backend runs at all.
///
/// <para><b>S3, the sleep.</b> <c>ClientPlatformAbstract</c> declares two neutral
/// virtuals, <c>window_RenderFrame</c> runs the client's own FPS cap only while no
/// backend owns it and calls <c>LatencySleep()</c> immediately before
/// <c>UpdateMousePosition()</c>, the patcher ships both members into the shipped DLL,
/// the OpenGL path keeps the neutral bodies, and VulkanClientPlatform overrides both
/// and self-checks them against the loaded lib.</para>
///
/// <para><b>S2, S4, S5, S7, S8, the renderer's side.</b> Checked in source because
/// the placement is the point - a marker one line later than the call it brackets
/// measures something else, and a second stamp of the same marker corrupts the
/// frame's report. The GPU tests (<c>LatencyMarkerOrderTests</c>,
/// <c>PresentIdentityTests</c>) prove the behaviour on a device; these pin where it
/// lives, so a refactor cannot quietly move a marker across the call it belongs
/// to.</para>
///
/// <para><b>The join between the stages.</b> The selection made at device creation
/// (S1) has to be the backend the frame's markers, submit tags, swapchain callbacks
/// and stats line actually run on (S2-S7), and the client's own FPS limiter may only
/// stand down because that backend says so (S3). Placement is the point here too:
/// installing the backend after the frame ring or after the first swapchain would
/// leave those two talking to a different instance than the one that was announced,
/// and nothing in a passing GPU test would say so.</para>
///
/// <para><b>The slots are coupled</b> (plan, user 2026-09-12): the latency selection
/// takes the active upscaler's vendor as an input, the client feeds it the real
/// setting, and the GPU vendor comes from the device properties. The decision table
/// itself is proven in
/// <c>Optimum.Render.Vulkan.Tests/LatencySlotCouplingTests.cs</c>; this is the
/// wiring, which no unit test can see once someone drops the argument at the call
/// site.</para>
/// </summary>
public class LatencySeamsCoverageTests
{
    private const string AbstractPath = "Vintagestory.Client.NoObf/ClientPlatformAbstract.cs";
    private const string RenderFrameSignature = "private void window_RenderFrame(FrameEventArgs e)";
    private const string DevicePath = "Optimum.Render.Vulkan/VulkanDevice.cs";
    private const string StatsPath = "Optimum.Render.Vulkan/Core/VulkanStats.cs";
    private const string FramePath = "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Frame.cs";
    private const string RingPath = "Optimum.Render.Vulkan/Core/FrameRing.cs";
    private const string SwapchainPath = "Optimum.Render.Vulkan/Present/Swapchain.cs";
    private const string StagesPath = "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Stages.cs";
    private const string PresentPathPath = "Optimum.Render.Vulkan/Present/IPresentPath.cs";

    // ---- S3: the sleep, and the client's frame cap -------------------------

    [Fact]
    public void TheFrameCapBlockIsGuardedByLatencyOwnsFrameCap()
    {
        string body = StripComments(Body(VulkanPlatformSource.ReadClientPlatformWindows(), RenderFrameSignature));

        // The effective cap is computed once, under the vanilla limiter's own conditions,
        // and 0 means uncapped - the number both pacing sides read.
        Match compute = Regex.Match(
            body, @"if \(ClientSettings\.VsyncMode != 1 && effectiveMaxFps > 10f && effectiveMaxFps < 241f\)");
        Assert.True(compute.Success, "the effective cap is no longer computed once:\n" + body);
        Assert.Contains("int latencyFrameCap = 0;", body);
        Assert.Contains("latencyFrameCap = (int)effectiveMaxFps;", body);

        // The one pacing block in the frame: the vanilla FPS limiter, now conditional.
        Match cap = Regex.Match(body, @"if \(!LatencyOwnsFrameCap && latencyFrameCap > 0\)");
        Assert.True(cap.Success, "the FPS cap block is gone or was reshaped:\n" + body);

        // Exactly one pacing block and exactly one read of the flag: a second one would
        // mean a second place that sleeps, which is the bug this seam exists to prevent.
        Assert.Single(Regex.Matches(body, @"ClientSettings\.VsyncMode != 1"));
        Assert.Single(Regex.Matches(body, @"LatencyOwnsFrameCap"));
    }

    /// <summary>
    /// Latency review follow-up, 2026-09-12: with the mode on by default the lib's own
    /// limiter stands down, so the cap it computed - the background-window reduction
    /// included - has to reach the pacing backend, exactly once, before the sleep it paces.
    /// Without it an unfocused window would run uncapped and burn the GPU in the background.
    /// </summary>
    [Fact]
    public void TheEffectiveFrameCapIsHandedOverOnceBeforeTheSleep()
    {
        string body = StripComments(Body(VulkanPlatformSource.ReadClientPlatformWindows(), RenderFrameSignature));

        int background = body.IndexOf("effectiveMaxFps = OptimumBgMaxFps;", StringComparison.Ordinal);
        int computed = body.IndexOf("int latencyFrameCap = 0;", StringComparison.Ordinal);
        int handOver = body.IndexOf("SetLatencyFrameCap(latencyFrameCap);", StringComparison.Ordinal);
        int limiter = body.IndexOf("!LatencyOwnsFrameCap", StringComparison.Ordinal);
        int sleep = body.IndexOf("LatencySleep();", StringComparison.Ordinal);

        Assert.True(background >= 0, "the background-window cap is gone:\n" + body);
        Assert.True(computed > background, "the cap is computed before the background reduction:\n" + body);
        Assert.True(handOver > computed, "the backend is handed a cap that was never computed:\n" + body);
        Assert.True(handOver < limiter, "the hand-over is not before the limiter block:\n" + body);
        Assert.True(sleep > handOver, "the cap reaches the backend after the sleep it paces:\n" + body);
        Assert.Single(Regex.Matches(body, @"SetLatencyFrameCap\("));
    }

    [Fact]
    public void LatencySleepIsCalledOnceAndBeforeTheInputSample()
    {
        string body = StripComments(Body(VulkanPlatformSource.ReadClientPlatformWindows(), RenderFrameSignature));

        int sleep = body.IndexOf("LatencySleep();", StringComparison.Ordinal);
        int mouse = body.IndexOf("UpdateMousePosition();", StringComparison.Ordinal);
        int cap = body.IndexOf("ClientSettings.VsyncMode != 1", StringComparison.Ordinal);
        int beginFrame = body.IndexOf("BeginFrame();", StringComparison.Ordinal);

        Assert.True(sleep >= 0, "LatencySleep() is not called in window_RenderFrame:\n" + body);
        Assert.True(mouse > sleep, "the input sample does not follow the sleep:\n" + body);
        Assert.True(sleep > cap, "the sleep runs before the frame cap block:\n" + body);
        Assert.True(beginFrame > mouse, "BeginFrame moved before the input sample:\n" + body);
        Assert.Single(Regex.Matches(body, @"LatencySleep\(\)"));
        Assert.Single(Regex.Matches(body, @"UpdateMousePosition\(\)"));
    }

    [Fact]
    public void WindowRenderFrameStaysCecilSafe()
    {
        string body = StripComments(Body(VulkanPlatformSource.ReadClientPlatformWindows(), RenderFrameSignature));
        Assert.DoesNotContain("=>", body);
        Assert.DoesNotContain("delegate", body);
        Assert.False(Regex.IsMatch(body, @"\.(All|Any|Where|Select|First|Count)\s*\("),
            "LINQ in a transplanted method:\n" + body);
    }

    [Fact]
    public void TheAbstractPlatformDeclaresNeutralVirtualsAndOpenGlDoesNotOverrideThem()
    {
        string platform = ReadLib(AbstractPath);
        Assert.Equal("{ }", Regex.Replace(Body(platform, "public virtual void LatencySleep()"), @"\s+", " ").Trim());
        Assert.Contains("public virtual bool LatencyOwnsFrameCap => false;", platform);
        Assert.Equal("{ }", Regex.Replace(
            Body(platform, "public virtual void SetLatencyFrameCap(int maxFps)"), @"\s+", " ").Trim());

        // The OpenGL platform calls all three but declares none, so vanilla pacing is untouched.
        string windows = VulkanPlatformSource.ReadClientPlatformWindows();
        Assert.DoesNotContain("override void LatencySleep", windows);
        Assert.DoesNotContain("override bool LatencyOwnsFrameCap", windows);
        Assert.DoesNotContain("override void SetLatencyFrameCap", windows);
    }

    [Fact]
    public void ThePatcherShipsBothMembers()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        string injected = Block(patcher, "[\"Vintagestory.Client.NoObf.ClientPlatformAbstract\"] = new()", "},");
        Assert.Contains("\"LatencySleep\",", injected);
        Assert.Contains("\"LatencyOwnsFrameCap\",", injected);
        Assert.Contains("\"SetLatencyFrameCap\",", injected);

        // The changed frame body has to be transplanted too, or the calls never ship.
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"window_RenderFrame\", 1),", patcher);
    }

    [Fact]
    public void TheVulkanPlatformSelfChecksAndOverridesBoth()
    {
        string selfCheck = Block(Read(VulkanPlatformSource.MainFile),
            "internal static readonly ExpectedVirtual[] ExpectedVirtuals", "};");
        Assert.Contains("new(true, \"LatencySleep\", Array.Empty<string>()),", selfCheck);
        Assert.Contains("new(true, \"get_LatencyOwnsFrameCap\", Array.Empty<string>()),", selfCheck);
        Assert.Contains("new(true, \"SetLatencyFrameCap\", new[] { \"Int32\" }),", selfCheck);

        string frame = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Frame.cs");
        string sleep = Body(frame, "public override void LatencySleep()");
        int sleepCall = sleep.IndexOf("backend.Sleep(frameId);", StringComparison.Ordinal);
        int input = sleep.IndexOf("LatencyMarker.InputSample", StringComparison.Ordinal);
        int simulation = sleep.IndexOf("LatencyMarker.SimulationStart", StringComparison.Ordinal);
        Assert.True(sleepCall >= 0, "the override does not call the backend's sleep:\n" + sleep);
        Assert.True(input > sleepCall, "InputSample is not stamped after the sleep:\n" + sleep);
        Assert.True(simulation > input, "SimulationStart is not stamped after InputSample:\n" + sleep);
        Assert.Contains("backend.OwnsFrameCap", Body(frame, "public override bool LatencyOwnsFrameCap"));

        // The cap hand-over: FPS in, the backend's MinimumIntervalUs out, applied only
        // when it changed, and never to a backend whose mode is Off.
        string cap = Body(frame, "public override void SetLatencyFrameCap(int maxFps)");
        Assert.Contains("if (current.Mode == LatencyMode.Off) return;", cap);
        Assert.Contains("ulong interval = FrameCapIntervalUs(maxFps);", cap);
        Assert.Contains("if (current.MinimumIntervalUs == interval) return;", cap);
        Assert.Contains("backend.Apply(new LatencySettings(current.Mode, interval));", cap);
        // Stage C owns the counter and made it public, because the lib hook is the
        // one site outside the renderer that opens a frame (seam S2).
        Assert.Contains("public ulong BeginLatencyFrame()", Read("Optimum.Render.Vulkan/VulkanDevice.cs"));
    }

    [Fact]
    public void TheLatencyModeSettingIsPersistedAndClamped()
    {
        // On by default since the 2026-09-12 acceptance runs (input-to-present 7.67 ms
        // off against 1.85 ms on NV Reflex and 1.84 ms on the vendor-neutral Native
        // backend), both in the live value and in the persisted data object, so a
        // config that never mentions the setting comes up on.
        foreach (string config in new[]
        {
            Read("VintagestoryApi/Config/OptimumConfig.cs"),
            Read("sources/VintagestoryApi/Config/OptimumConfig.cs"),
        })
        {
            Assert.Contains("public static string LatencyMode = \"on\";", config);
            Assert.Contains("public string LatencyMode { get; set; } = \"on\";", config);
            Assert.Contains("(nameof(OptimumConfigData.LatencyMode), LatencyMode),", config);
            Assert.Contains("LatencyMode = LatencyMode,", config);
            // Unrecognised values still degrade to a valid mode rather than failing the parse.
            Assert.Contains("string requestedLatencyMode = data.LatencyMode?.Trim() ?? \"\";", config);
            Assert.Contains("\"off\";", config);
        }
    }

    /// <summary>
    /// The round trip itself, through a real optimum.json: the default is on, a
    /// config that never mentions the setting comes up on, an explicit mode
    /// survives load and save, and an unknown one degrades to a valid mode.
    /// </summary>
    [Theory]
    [InlineData("\"on\"", "on")]
    [InlineData("\"boost\"", "boost")]
    [InlineData("\"off\"", "off")]
    [InlineData("\"BOOST\"", "boost")]
    [InlineData("\"  on  \"", "on")]
    [InlineData("\"nonsense\"", "off")]
    [InlineData("\"\"", "off")]
    [InlineData("null", "off")]
    [InlineData(null, "on")]
    public void TheLatencyModeRoundTripsThroughOptimumJson(string? writtenJsonValue, string expected)
    {
        string original = OptimumConfig.LatencyMode;
        string dataPath = Path.Combine(Path.GetTempPath(), "optimum-latency-" + Guid.NewGuid().ToString("N"));
        try
        {
            // SetDataPath re-reads the launcher's shader-compatibility report and treats a
            // missing one as "every shader feature disabled", which is global state the
            // rest of the assembly reads. A report that says the scan succeeded and
            // disabled nothing leaves that state exactly as a fresh process has it.
            Directory.CreateDirectory(Path.Combine(dataPath, ".optimum"));
            File.WriteAllText(Path.Combine(dataPath, ".optimum", "shader-compatibility.json"),
                "{ \"ScanFailed\": false, \"DisabledFeatures\": [] }");

            OptimumConfig.SetDataPath(dataPath);
            string configPath = Path.Combine(dataPath, "ModConfig", "optimum.json");
            // A config with no LatencyMode key at all is the upgrade case: it must come up on.
            File.WriteAllText(configPath, writtenJsonValue == null
                ? "{}"
                : "{ \"LatencyMode\": " + writtenJsonValue + " }");

            OptimumConfig.LatencyMode = "boost";
            OptimumConfig.Load();
            Assert.Equal(expected, OptimumConfig.LatencyMode);

            // Load writes the normalised value back, so the file now says what was loaded.
            Assert.Contains("\"LatencyMode\": \"" + expected + "\"", File.ReadAllText(configPath));

            // And it survives another trip through the file.
            OptimumConfig.LatencyMode = "boost";
            OptimumConfig.Load();
            Assert.Equal(expected, OptimumConfig.LatencyMode);
        }
        finally
        {
            OptimumConfig.LatencyMode = original;
            try { Directory.Delete(dataPath, recursive: true); } catch (IOException) { }
        }
    }

    // ---- S2, S4, S5, S7, S8: the renderer's side of the seams --------------

    [Fact]
    public void TheFrameIdIsAllocatedOncePerFrameAndIsNotTheCheckpointCounter()
    {
        string device = Read(DevicePath);

        // Seam S2: one allocator, and the checkpoint counter is untouched by it.
        Assert.Single(Regex.Matches(device, @"public ulong BeginLatencyFrame\(\)"));
        Assert.Contains("_latencyFrameId++;", device);
        Assert.Single(Regex.Matches(device, @"_latencyFrameId\+\+;"));
        Assert.Contains("private uint _frameCounter;", device);
        Assert.Contains("Checkpoint(Commands, CheckpointMarker.FrameBegin(_frameCounter));", device);

        // A frame without the lib hook still gets exactly one id.
        string beginFrame = MemberBody(device, "    public void BeginFrame()");
        Assert.Contains("if (!_latencyFrameIdPending) BeginLatencyFrame();", beginFrame);
        Assert.Contains("_latencyFrameIdPending = false;", beginFrame);
        Assert.Contains("_frames.Latency.FrameId = _latencyFrameId;", beginFrame);
    }

    [Fact]
    public void EveryMarkerIsStampedOnceAtItsCallSite()
    {
        string device = Read(DevicePath);
        string present = MemberBody(device, "    public void Present()");

        int submit = present.IndexOf("ulong renderValue = _frames.EndFrame();", StringComparison.Ordinal);
        int submitEnd = present.IndexOf("LatencyMarker.RenderSubmitEnd", StringComparison.Ordinal);
        int presentStart = present.IndexOf("LatencyMarker.PresentStart", StringComparison.Ordinal);
        int queuePresent = present.IndexOf("_swapchain.Present(target, _latencyFrameId);", StringComparison.Ordinal);
        int presentEnd = present.IndexOf("LatencyMarker.PresentEnd", StringComparison.Ordinal);
        int onPresent = present.IndexOf("Latency.OnPresent(_latencyFrameId, presentId);", StringComparison.Ordinal);

        Assert.True(submit >= 0 && submitEnd > submit, "RenderSubmitEnd must follow Submit A:\n" + present);
        Assert.True(presentStart > submitEnd && queuePresent > presentStart && presentEnd > queuePresent &&
                    onPresent > presentEnd,
            "PresentStart/End must bracket vkQueuePresentKHR, with OnPresent after them:\n" + present);

        // No marker twice, anywhere in the device.
        foreach (string marker in new[]
                 {
                     "LatencyMarker.RenderSubmitEnd", "LatencyMarker.PresentStart", "LatencyMarker.PresentEnd",
                     "LatencyMarker.SimulationEnd", "LatencyMarker.RenderSubmitStart",
                 })
        {
            Assert.Single(Regex.Matches(device, Regex.Escape(marker)));
        }

        // Seam S4: the first render stage of the frame stamps the pair, and only
        // the first, because the guard is the frame id itself.
        string renderStage = MemberBody(device, "    internal void NoteRenderStageStarted()");
        Assert.Contains("if (_latencyRenderStartFrame == _latencyFrameId) return;", renderStage);
        Assert.Contains("_latencyRenderStartFrame = _latencyFrameId;", renderStage);

        // The platform bracket asks on every stage; the listener decides.
        string stages = Read(StagesPath);
        Assert.Contains("ActiveLatencyStageListener()?.OnFrameRenderStart();", stages);
        Assert.Contains("internal ILatencyStageListener? LatencyStageListener;", stages);
        Assert.Single(Regex.Matches(stages, @"ActiveLatencyStageListener\(\)\?\.OnFrameRenderStart\(\);"));
    }

    [Fact]
    public void EveryFrameSubmitIsTaggedAndStandaloneUploadsAreNot()
    {
        string ring = Read(RingPath);
        string submit = MemberBody(ring, "    private void Submit(");
        Assert.Contains("void* chain = _latency.Backend.TagSubmit(_latency.FrameId, &timelineInfo);", submit);
        Assert.Contains("PNext = chain,", submit);

        // Submit A, Submit B and SubmitPartial all pass through that one method.
        foreach (string caller in new[]
                 {
                     "    public ulong SubmitPartial()",
                     "    public ulong EndFrameAndSubmit()",
                     "    public ulong SubmitPresent(",
                 })
        {
            Assert.Contains("Submit(", MemberBody(ring, caller));
        }

        string uploads = Read("Optimum.Render.Vulkan/Transfer/UploadManager.cs");
        Assert.DoesNotContain("TagSubmit", uploads);
    }

    [Fact]
    public void EverySwapchainCreationTellsTheBackendOnceAndOffersAPNextHook()
    {
        string swapchain = Read(SwapchainPath);
        string build = MemberBody(swapchain, "    private bool Build(out string? failureReason)");

        int created = build.IndexOf("_current = new SwapchainSlot(", StringComparison.Ordinal);
        int announced = build.IndexOf("_latency.OnSwapchainCreated(handle);", StringComparison.Ordinal);
        Assert.True(created >= 0 && announced > created,
            "the backend must be told after the slot exists:\n" + build);
        Assert.Contains("if (CreateChain != null) createInfo.PNext = CreateChain(createInfo.PNext);", build);

        // Once per creation: Build is the one creation path and the one caller.
        Assert.Single(Regex.Matches(swapchain, @"OnSwapchainCreated\(handle\);"));
        Assert.Single(Regex.Matches(swapchain, @"_current = new SwapchainSlot\("));

        // Seam S2: one present id per present, global so recreation cannot reset it.
        Assert.Contains("ulong presentId = PresentIdCounter.Next();", swapchain);
        Assert.Contains("PresentIds.Record(presentId, frameId);", swapchain);
        Assert.Contains("PNext = PresentIdEnabled ? &presentIdInfo : null,", swapchain);
        Assert.Single(Regex.Matches(swapchain, @"PresentIdCounter\.Next\(\)"));
    }

    [Fact]
    public void TheStatsSampleAlwaysCarriesTheLatencyLineAndTheSleepSite()
    {
        string stats = Read(StatsPath);
        Assert.Contains("LatencySleep = 9,", stats);
        Assert.Contains("\"latency_sleep\",", stats);
        Assert.Contains("public const int WaitSiteCount = 10;", stats);
        // Unconditional: no branch decides whether the line is written.
        Assert.Contains("LatencyLine(waitCounts[(int)WaitSite.LatencySleep], waitMs[(int)WaitSite.LatencySleep]) + \"\\n\" +", stats);

        string doc = File.ReadAllText(PatchReader.FindRepositoryFile("docs/taa-acceptance.md"));
        Assert.Contains("stats.latency", doc);
        Assert.Contains("`latency_sleep`", doc);
    }

    [Fact]
    public void ThePresentPathStatesTheBackendsItSupports()
    {
        string path = Read(PresentPathPath);
        Assert.Contains("LatencyBackendKind[] SupportedLatencyBackends { get; }", path);
        foreach (string kind in new[] { "None", "Native", "NvLowLatency2", "AmdAntiLag" })
        {
            Assert.Contains("LatencyBackendKind." + kind + ",", path);
        }
    }

    /// <summary>
    /// Latency review 2026-09-12. Two placements the review added, both of which
    /// a refactor could silently undo:
    /// the client's frame cap reaches a pacing backend at the sleep (without it,
    /// turning LatencyMode on stands the lib's limiter down and replaces it with
    /// nothing), and the swapchain tells the backend when the handle it holds is
    /// retired or destroyed, before the replacement is announced.
    /// </summary>
    [Fact]
    public void TheFrameCapAndTheSwapchainRetirementReachTheBackend()
    {
        string frame = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Frame.cs");

        // The cap arrives from the lib (window_RenderFrame's effective cap, background
        // reduction included) through its own injected virtual, ahead of the sleep.
        string apply = MemberBody(frame, "    public override void SetLatencyFrameCap(int maxFps)");
        Assert.Contains("ulong interval = FrameCapIntervalUs(maxFps);", apply);
        Assert.Contains("backend.Apply(new LatencySettings(current.Mode, interval));", apply);
        // Off is off: a disabled backend is never applied to, and an unchanged cap
        // never re-arms the driver's heuristic.
        Assert.Contains("if (current.Mode == LatencyMode.Off) return;", apply);
        Assert.Contains("if (current.MinimumIntervalUs == interval) return;", apply);

        // The sleep itself no longer computes a cap of its own - one source, one site.
        string sleep = MemberBody(frame, "    public override void LatencySleep()");
        Assert.DoesNotContain("ApplyFrameCap", sleep);
        Assert.Contains("backend.Sleep(frameId);", sleep);

        string swapchain = Read(SwapchainPath);
        // Once where the old slot is retired (a rebuild, failed or not), once at
        // teardown, and nowhere else.
        Assert.Equal(2, Regex.Matches(swapchain, @"_latency\.OnSwapchainRetired\(\);").Count);
        string build = MemberBody(swapchain, "    private bool Build(out string? failureReason)");
        int retired = build.IndexOf("_latency.OnSwapchainRetired();", StringComparison.Ordinal);
        int created = build.IndexOf("_latency.OnSwapchainCreated(handle);", StringComparison.Ordinal);
        Assert.True(retired >= 0, "a rebuild never tells the backend the old handle is gone:\n" + build);
        Assert.True(created > retired, "the new handle is announced before the old one is retired:\n" + build);
    }

    // ---- the join between the stages ---------------------------------------

    [Fact]
    public void TheSelectedBackendIsInstalledBeforeTheRingAndTheFirstSwapchain()
    {
        string device = Read(DevicePath);
        string initialize = MemberBody(device, "    public bool Initialize(IntPtr windowHandle, int width, int height, out string failureReason)");

        int context = initialize.IndexOf("_context = context!;", StringComparison.Ordinal);
        int install = initialize.IndexOf("InstallSelectedLatencyBackend();", StringComparison.Ordinal);
        // The ring takes its depth from the environment or a test override, so the
        // call is matched by its prefix rather than by the whole line.
        int ring = initialize.IndexOf("_frames = new FrameRing(_context", StringComparison.Ordinal);
        int swapchain = initialize.IndexOf("Swapchain.TryCreate(", StringComparison.Ordinal);

        Assert.True(context >= 0, "Initialize must take the context:\n" + initialize);
        Assert.True(install > context, "the backend is installed after the context exists:\n" + initialize);
        Assert.True(ring > install, "the frame ring must be built after the backend is installed:\n" + initialize);
        Assert.True(swapchain > install,
            "the first swapchain must be created after the backend is installed:\n" + initialize);

        // Exactly one install, so no second one can overwrite it mid-frame.
        Assert.Single(Regex.Matches(device, @"InstallSelectedLatencyBackend\(\);"));
    }

    [Fact]
    public void TheInstalledBackendIsTheOneTheCapabilitiesSelected()
    {
        string device = Read(DevicePath);
        string install = MemberBody(device, "    private void InstallSelectedLatencyBackend()");

        Assert.Contains("_context.Capabilities.LatencyBackend", install);
        Assert.Contains("SetLatencyBackend(backend);", install);
        Assert.Contains("backend.Apply(LatencySettingsFromConfig());", install);
        Assert.Contains("VulkanStats.LatencyRevision = _context.Capabilities.LatencySupport.NvLowLatency2SpecVersion;",
            install);

        // A selection with no implementation degrades and says so, rather than
        // silently running something else.
        Assert.Contains("if (backend.Kind != selected)", install);

        // A backend installed before Initialize is a deliberate choice and is not
        // overwritten by the selection.
        Assert.Contains("if (_latencyBackendInstalled)", install);

        // One place learns about the vendor backends when wave 3 lands.
        string factory = MemberBody(device, "    private ILatencyBackend CreateLatencyBackend(LatencyBackendKind kind)");
        Assert.Contains("return new NoneLatencyBackend(MirrorValidationMessage);", factory);

        // SetLatencyBackend is what makes the ring, the stats and the swapchain
        // agree; it must reach all three.
        string setter = MemberBody(device, "    internal void SetLatencyBackend(ILatencyBackend backend)");
        Assert.Contains("VulkanStats.LatencySource = Latency;", setter);
        Assert.Contains("_frames.Latency.Backend = Latency;", setter);
        Assert.Contains("_swapchain.Latency = Latency;", setter);
    }

    [Fact]
    public void TheClientsPersistedSettingIsWhatTheBackendIsAskedFor()
    {
        string device = Read(DevicePath);
        string settings = MemberBody(device, "    private static LatencySettings LatencySettingsFromConfig()");

        Assert.Contains("OptimumConfig.LatencyEnabled", settings);
        Assert.Contains("OptimumConfig.LatencyBoost", settings);
        Assert.Contains("return LatencySettings.Disabled;", settings);
        Assert.Contains("LatencyMode.Boost : LatencyMode.On", settings);
    }

    [Fact]
    public void TheFrameCapFollowsTheInstalledBackend()
    {
        string frame = Read(FramePath);
        string owns = MemberBody(frame, "    public override bool LatencyOwnsFrameCap");

        // S3: the lib's limiter stands down only because the live backend says
        // it paces the frame itself, and the live backend is the device's.
        Assert.Contains("backend.OwnsFrameCap", owns);
        Assert.Contains("ILatencyBackend? LatencyBackend => LatencyBackendOverride ?? device?.Latency;", frame);
    }

    [Fact]
    public void TheDeviceUpLineAndTheStatsLineBothNameTheBackend()
    {
        string device = Read(DevicePath);
        Assert.Contains("_context.Capabilities.LatencySummary", device);

        string stats = Read(StatsPath);
        // Seam S7: backend, mode and the vendor revision behind it.
        Assert.Contains("public static volatile uint LatencyRevision;", stats);
        Assert.Contains("line.Append(\" rev=\")", stats);
        Assert.Contains("FormatLatencyLine(backend, mode, LatencyRevision,", stats);

        // Every token of the line is documented.
        string doc = File.ReadAllText(PatchReader.FindRepositoryFile("docs/taa-acceptance.md"));
        Assert.Contains("rev=", doc);

        // A disposed device leaves nothing behind for the next one's line.
        Assert.Contains("VulkanStats.LatencyRevision = 0;", device);
    }

    [Fact]
    public void PresentIdsAreChainedOnlyWhereTheFeatureWasEnabled()
    {
        string device = Read(DevicePath);
        Assert.Contains("_swapchain.PresentIdEnabled = _context.Capabilities.PresentIdEnabled;", device);
        Assert.Single(Regex.Matches(device, @"_swapchain\.PresentIdEnabled = "));
    }

    // ---- the slots are coupled ---------------------------------------------

    [Fact]
    public void TheSelectorTakesBothVendorsAndLogsTheDecision()
    {
        string coupling = Read("Optimum.Render.Vulkan/Latency/LatencySlotCoupling.cs");
        Assert.Contains("public static LatencyBackendKind? Requested(", coupling);
        Assert.Contains("UpscalerVendor upscaler, GpuVendor gpu, LatencyPresentPath path, out string reason", coupling);
        // The GPU half is the vendor id from VkPhysicalDeviceProperties.
        Assert.Contains("public const uint NvidiaVendorId = 0x10DE;", coupling);
        Assert.Contains("public const uint AmdVendorId = 0x1002;", coupling);
        Assert.Contains("public const uint IntelVendorId = 0x8086;", coupling);
        // One line naming the pair and the decision.
        Assert.Contains("\"latency: upscaler \" + UpscalerVendors.Token(upscaler) +", coupling);

        string selector = Read("Optimum.Render.Vulkan/Latency/LatencyBackendSelector.cs");
        Assert.Contains("UpscalerVendor upscaler,", selector);
        Assert.Contains("GpuVendor gpu,", selector);
        Assert.Contains("LatencySlotCoupling.Requested(upscaler, gpu, path, out string reason)", selector);
        // OPTIMUM_VULKAN_LATENCY wins over the coupling.
        Assert.Contains("LatencyBackendKind? request = forced ?? coupled;", selector);
        Assert.Contains("LatencySlotCoupling.DecisionLine(upscaler, gpu, selected, reason)", selector);
    }

    [Fact]
    public void TheClientFeedsTheSelectionTheActiveUpscalerAndTheDeviceVendor()
    {
        string device = Read("Optimum.Render.Vulkan/VulkanDevice.cs");
        Assert.Contains(
            "UpscalerVendor = UpscalerVendors.FromSettingToken(OptimumConfig.EffectiveUpscaler),", device);

        string context = Read("Optimum.Render.Vulkan/Core/VulkanContext.cs");
        Assert.Contains("public UpscalerVendor UpscalerVendor = UpscalerVendor.None;", context);
        Assert.Contains("options.UpscalerVendor,", context);
        // The vendor the coupling matches against is a device property.
        Assert.Contains("VendorId = properties.VendorID,", context);
        Assert.Contains("Vendor = GpuVendors.FromVendorId(properties.VendorID),", context);
    }

    /// <summary>
    /// Where the two roadmap streams meet: the passthrough comparison upscaler is
    /// an upscaler with no vendor stack, not "no upscaler". It has to be its own
    /// vendor value, because the two answers differ - "no upscaler" leaves the
    /// device auto order (Reflex on an NVIDIA box) and a vendor-less upscaler
    /// takes Optimum's own pacing, which is what keeps passthrough-vs-DLSS a
    /// comparison of the reconstruction alone.
    /// </summary>
    [Fact]
    public void ThePassthroughUpscalerIsVendorLessRatherThanNoUpscaler()
    {
        string coupling = Read("Optimum.Render.Vulkan/Latency/LatencySlotCoupling.cs");

        // Its own enum value, and the setting token maps to it.
        Assert.Contains("VendorLess = 4,", coupling);
        Assert.Contains("case \"passthrough\":\n                return UpscalerVendor.VendorLess;",
            coupling.Replace("\r\n", "\n"));

        // And it asks for our own pacing, before the vendor-match question is
        // ever reached - there is no vendor to match.
        Assert.Contains(
            "if (upscaler == UpscalerVendor.VendorLess)\n        {\n" +
            "            reason = \"vendor-less upscaler; Optimum's own pacing\";\n" +
            "            return LatencyBackendKind.Native;\n        }",
            coupling.Replace("\r\n", "\n"));

        // "off" keeps the untouched auto order, so the two really are distinct.
        Assert.Contains("reason = \"no upscaler; device auto order\";", coupling);
        Assert.Contains("return null;", coupling);

        // The passthrough token is the one the config actually persists.
        string config = Read("VintagestoryApi/Config/OptimumConfig.cs");
        Assert.Contains("UpscalerNames = { \"off\", \"dlss\", \"passthrough\" }", config);
    }

    // ---- helpers -----------------------------------------------------------

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));

    private static string ReadLib(string relativePath)
    {
        try
        {
            return File.ReadAllText(PatchReader.FindRepositoryFile("build/VintagestoryLib/" + relativePath));
        }
        catch (FileNotFoundException)
        {
            return PatchReader.ReadPatchedContent(PatchReader.FindRepositoryFile(
                "patches/VintagestoryLib/" + relativePath + ".patch"));
        }
    }

    private static string StripComments(string source) =>
        Regex.Replace(source, @"//[^\n]*", string.Empty);

    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing: " + signature);
        int open = source.IndexOf('{', start + signature.Length);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
        }
        throw new InvalidOperationException("unbalanced body: " + signature);
    }

    private static string Block(string source, string header, string terminator)
    {
        int start = source.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing: " + header);
        int end = source.IndexOf(terminator, start, StringComparison.Ordinal);
        Assert.True(end > start);
        return source.Substring(start, end - start);
    }

    /// <summary>The member starting at <paramref name="signature" />, up to its closing brace.</summary>
    private static string MemberBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing " + signature);
        int end = source.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        Assert.True(end > start, "unterminated " + signature);
        return source.Substring(start, end - start);
    }
}
