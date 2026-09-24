// Source: Optimum.Tests/latency-hooks-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// Vulkan-native plan, "Latency seams" S3: the sleep happens before the client samples input.
///
/// <c>ClientPlatformAbstract</c> declares two neutral virtuals, <c>window_RenderFrame</c> runs
/// the client's own FPS cap only while no backend owns it and calls <c>LatencySleep()</c>
/// immediately before <c>UpdateMousePosition()</c>, the patcher ships both members into the
/// shipped DLL, the OpenGL path keeps the neutral bodies, and VulkanClientPlatform overrides
/// both and self-checks them against the loaded lib.
/// </summary>
public class LatencyHooksCoverageTests
{
    private const string AbstractPath = "Vintagestory.Client.NoObf/ClientPlatformAbstract.cs";
    private const string RenderFrameSignature = "private void window_RenderFrame(FrameEventArgs e)";

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
    /// Once a pacing backend owns the cap the lib's own limiter stands down, so the cap it
    /// computed - the background-window reduction included - has to reach the backend,
    /// exactly once, before the sleep it paces. Without it an unfocused window would run
    /// uncapped and burn the GPU in the background.
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
        string patcher = PatcherSource.Read();

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
        int sleepCall = sleep.IndexOf("backend.Sleep(frameId)", StringComparison.Ordinal);
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
        // The device owns the counter and it is public, because the lib hook is the one
        // site outside the renderer that opens a frame (seam S2).
        Assert.Contains("public ulong BeginLatencyFrame()", Read("Optimum.Render.Vulkan/VulkanDevice.Latency.cs"));
    }

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
        source = source.Replace("\r\n", "\n", StringComparison.Ordinal);
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
}
}

// Source: Optimum.Tests/latency-renderer-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// Latency seams, the frame-marking foundation (plan section "Latency seams", S1, S2,
/// S4, S5, S7): the
/// renderer's side of the seams, checked in source because the placement is the
/// point - a marker one line later than the call it brackets measures something
/// else, and a second stamp of the same marker corrupts the frame's report.
///
/// The GPU tests (LatencyMarkerOrderTests, PresentIdentityTests) prove the
/// behaviour on a device; these pin where it lives, so a refactor cannot quietly
/// move a marker across the call it belongs to.
/// </summary>
public class LatencyRendererCoverageTests
{
    private const string DevicePath = "Optimum.Render.Vulkan/VulkanDevice.cs";
    private const string DeviceLatencyPath = "Optimum.Render.Vulkan/VulkanDevice.Latency.cs";
    private const string RequirementsPath = "Optimum.Render.Vulkan/Latency/LatencyDeviceRequirements.cs";
    private const string ContextPath = "Optimum.Render.Vulkan/Core/VulkanContext.cs";
    private const string RingPath = "Optimum.Render.Vulkan/Core/FrameRing.cs";
    private const string SwapchainPath = "Optimum.Render.Vulkan/Present/Swapchain.cs";
    private const string StagesPath = "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Stages.cs";
    private const string StatsPath = "Optimum.Render.Vulkan/Core/VulkanStats.cs";

    [Fact]
    public void TheFrameIdIsAllocatedOncePerFrameAndIsNotTheCheckpointCounter()
    {
        string device = VulkanDeviceSource.Read() + Read(DeviceLatencyPath);

        // Seam S2: one allocator, and the checkpoint counter is untouched by it.
        Assert.Single(Regex.Matches(device, @"public ulong BeginLatencyFrame\(\)"));
        Assert.Contains("_latencyFrameId++;", device);
        Assert.Single(Regex.Matches(device, @"_latencyFrameId\+\+;"));
        Assert.Contains("private uint _frameCounter;", device);
        Assert.Contains("Checkpoint(Commands, CheckpointMarker.FrameBegin(_frameCounter));", device);

        // A frame without the lib hook still gets exactly one id, before the ring hands out the slot.
        string beginFrame = Body(VulkanDeviceSource.Read(), "    public void BeginFrame()");
        int identity = beginFrame.IndexOf("BeginLatencyFrameIdentity();", StringComparison.Ordinal);
        int slot = beginFrame.IndexOf("FrameSlot slot = _frames.BeginFrame();", StringComparison.Ordinal);
        Assert.True(identity >= 0 && slot > identity, "the frame id is not taken before the slot:\n" + beginFrame);
        string take = Body(Read(DeviceLatencyPath), "    private void BeginLatencyFrameIdentity()");
        Assert.Contains("if (!_latencyFrameIdPending) BeginLatencyFrame();", take);
        Assert.Contains("_latencyFrameIdPending = false;", take);
        Assert.Contains("_frames.Latency.FrameId = _latencyFrameId;", take);
    }

    [Fact]
    public void EveryMarkerIsStampedOnceAtItsCallSite()
    {
        string device = VulkanDeviceSource.Read() + Read(DeviceLatencyPath);
        string present = Body(VulkanDeviceSource.Read(), "    public void Present()");

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
        string renderStage = Body(Read(DeviceLatencyPath), "    internal void NoteRenderStageStarted()");
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
        string submit = Body(ring, "    private void Submit(");
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
            Assert.Contains("Submit(", Body(ring, caller));
        }

        string uploads = Read("Optimum.Render.Vulkan/Transfer/UploadManager.cs");
        Assert.DoesNotContain("TagSubmit", uploads);
    }

    [Fact]
    public void EverySwapchainCreationTellsTheBackendOnceAndOffersAPNextHook()
    {
        string swapchain = Read(SwapchainPath);
        string build = Body(swapchain, "    private bool Build(out string? failureReason)");

        int created = build.IndexOf("_current = new SwapchainSlot(", StringComparison.Ordinal);
        int announced = build.IndexOf("Latency.OnSwapchainCreated(handle);", StringComparison.Ordinal);
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

    /// <summary>
    /// Seam S1 on this branch: the None backend is the only one, and present ids are
    /// enabled on their own merit (supported, presentable), not as a vendor backend's
    /// dependency - the frame identity reaches the display with no pacing backend. The
    /// contributors share one pNext chain with device fault and the colour-write tier.
    /// </summary>
    [Fact]
    public void PresentIdsAreEnabledWithoutAPacingBackendAndOnlyNoneIsConstructed()
    {
        string requirements = Read(RequirementsPath);
        string contribute = Body(requirements, "    public void ContributeDeviceRequirements(DeviceRequirements requirements)");
        Assert.Contains("bool presentable = requirements.IsEnabled(SwapchainExtensionName);", contribute);
        Assert.Contains("PresentIdEnabled = presentable && Support.PresentId", contribute);
        Assert.Contains("PresentId = true,", contribute);
        // Detection only for the vendor extensions: nothing requests them here.
        Assert.DoesNotContain("Request(NvLowLatency2ExtensionName", contribute);
        Assert.DoesNotContain("Request(AmdAntiLagExtensionName", contribute);
        Assert.Contains("public LatencyBackendKind Selected => LatencyBackendKind.None;", requirements);

        string install = Body(Read(DeviceLatencyPath), "    private void InstallSelectedLatencyBackend()");
        Assert.Contains("new NoneLatencyBackend(MirrorValidationMessage)", install);
        Assert.Single(Regex.Matches(install, @"new \w+LatencyBackend\("));

        string context = Read(ContextPath);
        Assert.Contains("contributor.ContributeDeviceRequirements(requirements);", context);
        Assert.Contains("vulkan13.PNext = requirements.Chain;", context);
        Assert.DoesNotContain("optionalFeatures", context);

        // The swapchain only chains VkPresentIdKHR when the capability says it may.
        Assert.Contains("_swapchain!.PresentIdEnabled = _context.Capabilities.PresentIdEnabled;", VulkanDeviceSource.Read());
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
        string apply = Body(frame, "    public override void SetLatencyFrameCap(int maxFps)");
        Assert.Contains("ulong interval = FrameCapIntervalUs(maxFps);", apply);
        Assert.Contains("backend.Apply(new LatencySettings(current.Mode, interval));", apply);
        // Off is off: a disabled backend is never applied to, and an unchanged cap
        // never re-arms the driver's heuristic.
        Assert.Contains("if (current.Mode == LatencyMode.Off) return;", apply);
        Assert.Contains("if (current.MinimumIntervalUs == interval) return;", apply);

        // The sleep itself no longer computes a cap of its own - one source, one site.
        string sleep = Body(frame, "    public override void LatencySleep()");
        Assert.DoesNotContain("ApplyFrameCap", sleep);
        Assert.Contains("backend.Sleep(frameId)", sleep);

        string swapchain = Read(SwapchainPath);
        // Once where the old slot is retired (a rebuild, failed or not), once at
        // teardown, and nowhere else.
        Assert.Equal(2, Regex.Matches(swapchain, @"Latency\.OnSwapchainRetired\(\);").Count);
        string build = Body(swapchain, "    private bool Build(out string? failureReason)");
        int retired = build.IndexOf("Latency.OnSwapchainRetired();", StringComparison.Ordinal);
        int created = build.IndexOf("Latency.OnSwapchainCreated(handle);", StringComparison.Ordinal);
        Assert.True(retired >= 0, "a rebuild never tells the backend the old handle is gone:\n" + build);
        Assert.True(created > retired, "the new handle is announced before the old one is retired:\n" + build);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));

    /// <summary>The member starting at <paramref name="signature" />, up to its closing brace.</summary>
    private static string Body(string source, string signature)
    {
        source = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing " + signature);
        int end = source.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        Assert.True(end > start, "unterminated " + signature);
        return source.Substring(start, end - start);
    }
}
}

// Source: Optimum.Tests/pacing-log-format-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// The OPTIMUM_FPS_LOG line has one producer (ClientMain.OptimumLogFrameTime) and
/// three readers: perf-capture.sh, pacing-gate.sh and the acceptance document.
/// These tests render the producer's own format string and make every reader
/// agree with it, so a field added on one side cannot silently fall off another
/// (perf-capture.sh's Vulkan-stats regex had already drifted that way once).
/// </summary>
public class PacingLogFormatCoverageTests
{
    private static readonly string[] FpsKeys = { "window", "frames", "mean", "min", "max", "p99", "stddev" };

    private static string ClientMain() => ReadPatchedOrSource(
        "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs.patch",
        "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs");

    private static string FpsFormatString()
    {
        Match match = Regex.Match(ClientMain(), "\"(\\[Optimum\\] fps window=[^\"]*)\"");
        Assert.True(match.Success, "no [Optimum] fps format string in ClientMain");
        return match.Groups[1].Value;
    }

    private static string ScriptFpsRegex(string script)
    {
        Match match = Regex.Match(Read(script), "FPS_LINE_RE = re\\.compile\\(r\"(.*)\"\\)");
        Assert.True(match.Success, "no single-line FPS_LINE_RE in " + script);
        // Python spells a named group (?P<name>...); .NET spells it (?<name>...).
        return match.Groups[1].Value.Replace("(?P<", "(?<", StringComparison.Ordinal);
    }

    private static List<string> Keys(string text, string pattern)
    {
        var keys = new List<string>();
        foreach (Match match in Regex.Matches(text, pattern)) keys.Add(match.Groups[1].Value);
        return keys;
    }

    [Fact]
    public void FpsLineFormatAppendsStddevAfterP99()
    {
        string format = FpsFormatString();
        Assert.Equal(
            "[Optimum] fps window={0:F3} frames={1} mean={2:F3} min={3:F3} max={4:F3} p99={5:F3} stddev={6:F3}",
            format);
        Assert.Equal(FpsKeys, Keys(format, @"(\w+)=\{"));
    }

    [Fact]
    public void ClientStddevIsComputedOverTheSameWindowWithoutHelperTypes()
    {
        string clientMain = ClientMain();
        Assert.Contains("double stddev = Math.Sqrt(squares / (double)sampled);", clientMain);
        // Seven loose arguments would bind string.Format's params ReadOnlySpan
        // overload, which lowers into an InlineArray helper type the Cecil
        // transplant cannot carry.
        Assert.Contains("object[] fields = new object[7];", clientMain);
        Assert.Contains("fields[6] = stddev;", clientMain);

        string patcher = PatcherSource.Read();
        Assert.Contains("\"OptimumLogFrameTime\"", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"MainRenderLoop\", 1", patcher);
    }

    [Fact]
    public void BothScriptsParseTheRenderedLineAndOldLogs()
    {
        string format = FpsFormatString();
        string rendered = string.Format(CultureInfo.InvariantCulture, format,
            1.004, 120, 8.367, 7.912f, 11.204f, 10.811f, 0.612);
        string old = rendered.Substring(0, rendered.IndexOf(" stddev=", StringComparison.Ordinal));

        string capture = ScriptFpsRegex("scripts/dev/perf-capture.sh");
        string gate = ScriptFpsRegex("scripts/dev/pacing-gate.sh");
        Assert.Equal(capture, gate);
        Assert.Equal(FpsKeys, Keys(gate, @"\(\?<(\w+)>"));

        Match match = Regex.Match("2026-09-11 " + rendered, gate);
        Assert.True(match.Success, rendered);
        Assert.Equal("1.004", match.Groups["window"].Value);
        Assert.Equal("120", match.Groups["frames"].Value);
        Assert.Equal("10.811", match.Groups["p99"].Value);
        Assert.Equal("0.612", match.Groups["stddev"].Value);

        Match oldMatch = Regex.Match(old, gate);
        Assert.True(oldMatch.Success, old);
        Assert.False(oldMatch.Groups["stddev"].Success);
    }

    [Fact]
    public void AcceptanceDocumentShowsTheSameFieldsInTheSameOrder()
    {
        string doc = Read("docs/taa-acceptance.md");
        string? template = null;
        foreach (string line in doc.Split('\n'))
        {
            if (line.Contains("[Optimum] fps window=<", StringComparison.Ordinal))
            {
                template = line;
                break;
            }
        }
        Assert.NotNull(template);
        Assert.Equal(FpsKeys, Keys(template!, @"(\w+)=<"));

        Assert.Contains("scripts/dev/pacing-gate.sh", doc);
        foreach (string rule in new[]
                 { "blocking_uploads", "stddev_vs_baseline", "p99_vs_mean", "dropped_mesh_writes", "uniform_overflows" })
        {
            Assert.Contains("`" + rule + "`", doc);
        }
    }

    [Fact]
    public void PerfCapturePrintsStddevAndReadsTheRealStatsText()
    {
        string script = Read("scripts/dev/perf-capture.sh");
        Assert.Contains("frame stddev", script);
        Assert.Contains("stddev_ms", script);
        // VulkanStats writes "N frames (M ms/frame)"; there never was a frameMs token.
        Assert.Contains(@"ms/frame\)", script);
        Assert.DoesNotContain("frameMs[= ]", script);
        Assert.Contains("scripts/dev/pacing-gate.sh", script);
    }

    [Fact]
    public void PacingGateSelfTestPasses()
    {
        (int code, string output) = RunGate("--self-test");
        Assert.True(code == 0, output);
        Assert.Contains("cases passed", output);
        Assert.DoesNotContain("FAIL ", output);
    }

    [Fact]
    public void PacingGateAcceptsTheClientLineOnAnOpenGlRunWithoutStats()
    {
        string format = FpsFormatString();
        string dir = Directory.CreateTempSubdirectory("optimum-pacing-").FullName;
        try
        {
            string fps = Path.Combine(dir, "fps.log");
            string line = string.Format(CultureInfo.InvariantCulture, format,
                1.004, 120, 8.367, 7.912f, 11.204f, 10.811f, 0.612);
            File.WriteAllText(fps, line + "\n" + line + "\n" + line + "\n");

            (int code, string output) = RunGate("--renderer", "opengl", "--fps", fps, "--baseline", fps);
            Assert.True(code == 0, output);
            Assert.Contains("median stddev 0.612 ms", output);
            Assert.Matches(new Regex(@"stddev_vs_baseline\s+0\.612 ms.*PASS"), output);
            Assert.Matches(new Regex(@"blocking_uploads\s+-\s+no --stats\s+SKIP"), output);

            // A Vulkan run is not gateable without its stats file.
            (code, output) = RunGate("--renderer", "vulkan", "--fps", fps);
            Assert.True(code == 2, output);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void PacingGateNeverPatternKills()
    {
        string script = Read("scripts/dev/pacing-gate.sh");
        Assert.DoesNotContain("pkill", script);
        Assert.DoesNotContain("pgrep", script);
        Assert.Contains("\nset -euo pipefail\n", script);

        string capture = Read("scripts/dev/perf-capture.sh");
        Assert.Contains("\nset -euo pipefail\n", capture);
        // Under pipefail a missing renderer line must reach the refusal, not end the script.
        Assert.Contains("awk '{print $2}' || true)\"", capture);
    }

    private static (int Code, string Output) RunGate(params string[] arguments)
    {
        string script = PatchReader.FindRepositoryFile("scripts/dev/pacing-gate.sh");
        var start = new ProcessStartInfo(TestToolchain.Bash)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(script);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        using Process process = Process.Start(start)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr);
    }

    private static string ReadPatchedOrSource(string patchPath, string sourcePath)
    {
        string? resolvedPatch = TryFind(patchPath);
        return resolvedPatch != null ? PatchReader.ReadPatchedContent(resolvedPatch) : Read(sourcePath);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));

    private static string? TryFind(string relativePath)
    {
        try
        {
            return PatchReader.FindRepositoryFile(relativePath);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
}
