using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

using Xunit;

namespace Optimum.Tests;

/// <summary>
/// The upscaler's and NGX's lifetime: bring-up, teardown, the one lifetime a
/// process gets, and the native shim that lifetime runs through.
///
/// <para><b>The orderings (DLSS plan, Phase 2 step 4).</b> Four of them decide
/// whether this works or takes the process down, and none can be observed from a
/// unit test on a headless host, so each is pinned against the source that
/// carries it:
/// <list type="number">
/// <item>the session is prepared <b>before</b> the device is created, because
/// NGX's instance and device extensions are requested at device creation;</item>
/// <item>NGX is initialised <b>after</b> the device exists, and a refusal leaves
/// the client on the Vulkan device without an upscaler rather than failing the
/// install;</item>
/// <item>teardown is retire the feature, drain the frame timeline, shut NGX
/// down, <b>then</b> destroy the device - releasing a feature after
/// <c>Shutdown1</c>, or shutting NGX down after its device is gone, is a
/// use-after-free inside the driver;</item>
/// <item>NGX is never brought up twice in a process: the second
/// <c>Shutdown1</c> segfaults (measured 2026-09-12, driver 615.71.09).</item>
/// </list></para>
///
/// <para><b>The crash of 2026-09-12.</b> <c>NVSDK_NGX_VULKAN_Shutdown1</c> is
/// declared with one parameter in the SDK header and implemented with two in
/// driver 615.71.09: it forwards its second argument into the internal shutdown
/// routine, which stores the SDK's remaining reference count through it without a
/// null check. Called through the header's prototype, that pointer is whatever the
/// caller left in <c>%rsi</c> - 8192 in both core dumps - and NGX writes four bytes
/// to it. The shim therefore always passes an <c>int*</c> of its own.</para>
///
/// <para><b>The lifetime.</b> Independently of the crash, the one NGX lifetime a
/// process gets has exactly one owner: <c>NgxLifetime</c>. It performs the release
/// and the drain itself, refuses a second shutdown and refuses to bring NGX up
/// again - so no settings change, and no second teardown, can reach
/// <c>Shutdown1</c>. The gate on that shutdown is "was NGX brought up", never "does
/// the upscaler still work" (PR #3 review).</para>
///
/// <para><b>The shim build.</b> The lifetime only exists if the native shim that
/// carries it was built and shipped; a shim that is stale, half-built or tail-called
/// into is an ABI mismatch inside NGX rather than the "DLSS unavailable" line the
/// client is supposed to degrade to, so the two build hosts (the csproj's
/// <c>Exec</c> and <c>build.sh</c>) are pinned here as well.</para>
///
/// <para>These are source-coverage tests in the sense the repo uses the phrase -
/// they hold a shape that a later edit would otherwise quietly undo. The
/// behavioural proofs live next to the behaviour: <c>TimelineLifetimeTests</c> and
/// <c>FrameRingTests</c> for the teardown contract, and the GPU tests for the frame
/// path.</para>
/// </summary>
public class UpscalerLifecycleCoverageTests
{
    private const string Upscaler = "Optimum.Render.Vulkan/Upscale/DlssUpscaler.cs";
    private const string Session = "Optimum.Render.Vulkan/Upscale/Ngx/NgxSession.cs";
    private const string Ring = "Optimum.Render.Vulkan/Core/FrameRing.cs";
    private const string Project = "Optimum.Render.Vulkan/Optimum.Render.Vulkan.csproj";
    private const string GpuTests = "Optimum.Render.Vulkan.Tests/DlssUpscalerTests.cs";
    private const string PassthroughGpuTests = "Optimum.Render.Vulkan.Tests/PassthroughUpscalerTests.cs";

    // ---- bring-up and teardown ordering ------------------------------------

    [Fact]
    public void TheSessionIsPreparedBeforeTheDeviceAndNgxComesUpAfterIt()
    {
        string platform = VulkanPlatformSource.Read();

        int factory = platform.IndexOf("device = DeviceFactory();", StringComparison.Ordinal);
        int prepare = platform.IndexOf("PrepareUpscaler(device);", StringComparison.Ordinal);
        int initialize = platform.IndexOf("if (!device.Initialize(windowHandle", StringComparison.Ordinal);
        int bringUp = platform.IndexOf("BringUpUpscaler(device);", StringComparison.Ordinal);

        Assert.True(factory > 0 && prepare > factory,
            "the upscaler must be prepared on the device object that was just created");
        Assert.True(initialize > prepare,
            "the requirement contributor must be added before device.Initialize, or NGX's device " +
            "extensions never reach the created device");
        Assert.True(bringUp > initialize, "NGX must be initialised after the device exists");

        // The contributor is chained onto whatever was configured before, never
        // assigned over it.
        Assert.Contains("Action<VulkanContextOptions>? configured = target.ConfigureContextOptions;", platform);
        Assert.Contains("options.RequirementContributors.Add(requirements);", platform);

        // A refusal is not a failed install: the device stays, the host goes.
        Assert.Contains("if (!upscaler.BringUp(target, instance, physicalDevice, deviceHandle))", platform);
        Assert.Contains("upscaler.Dispose();", platform);
        Assert.Contains("upscaler = null;", platform);
    }

    [Fact]
    public void TheVendorRuntimeIsShutDownBeforeTheDeviceItWasInitialisedOn()
    {
        string platform = VulkanPlatformSource.Read();

        int shutdownGraphics = platform.IndexOf("public override void ShutdownGraphics()", StringComparison.Ordinal);
        Assert.True(shutdownGraphics > 0);
        string body = platform.Substring(shutdownGraphics);

        int upscaler = body.IndexOf("ShutDownUpscaler();", StringComparison.Ordinal);
        int dispose = body.IndexOf("device?.Dispose();", StringComparison.Ordinal);
        Assert.True(upscaler > 0, "ShutdownGraphics does not shut the upscaler down");
        Assert.True(dispose > upscaler,
            "the upscaler must go before the device it was initialised on:\n" + body.Substring(0, 800));
    }

    /// <summary>
    /// The host's own order, which is the one the driver actually sees: the
    /// feature is retired onto the frame timeline, the timeline is drained so
    /// nothing still names its handle, and only then is NGX shut down.
    /// </summary>
    [Fact]
    public void TheHostRetiresAndDrainsBeforeItShutsNgxDown()
    {
        string host = Read(Upscaler);
        int shutdown = host.IndexOf("public void Shutdown()", StringComparison.Ordinal);
        Assert.True(shutdown > 0);
        string body = host.Substring(shutdown);

        // The order is no longer written out at this call site: it is performed by
        // the one owner, which is handed the release and the drain to run itself.
        Assert.Contains(
            "NgxLifetime.ShutDown(RetireFeature, device != null ? device.DrainDeferredDeletions : null, _log);",
            body);

        string owner = Read("Optimum.Render.Vulkan/Upscale/Ngx/NgxLifetime.cs");
        int release = owner.IndexOf("if (releaseFeatures != null) releaseFeatures();", StringComparison.Ordinal);
        int drain = owner.IndexOf("if (drainFrameTimeline != null) drainFrameTimeline();", StringComparison.Ordinal);
        // The handle is copied into a local before the call, so that an exception out
        // of native code cannot leave the owner both Spent and Initialized; match the
        // call rather than its argument.
        int ngx = owner.IndexOf("ShutdownResult = _shutdownCall(", StringComparison.Ordinal);
        Assert.True(release > 0 && drain > release && ngx > drain,
            "the owner must release, drain and only then shut NGX down");
        int cleared = owner.IndexOf("_device = IntPtr.Zero;", ngx - 200 > 0 ? ngx - 200 : 0, StringComparison.Ordinal);
        Assert.True(cleared > 0 && cleared < ngx,
            "the owner must clear its state before it calls into NGX, so a throw cannot leave it half shut down");

        // One lifetime per process, enforced in both directions.
        Assert.Contains("if (_shutDown) return NgxLifetimeOutcome.AlreadyShutDown;", owner);
        Assert.Contains(
            "return Fail(\"NGX was already shut down in this process and allows exactly one lifetime\");", host);

        // A feature is never destroyed inline while an evaluate may still name it.
        Assert.Contains("if (_device != null) _device.RetireDlssFeature(feature);", host);
    }

    /// <summary>
    /// A plan change - a resize or a preset change - retires the old feature
    /// before creating the new one, which is what keeps a resize from leaking
    /// features.
    /// </summary>
    [Fact]
    public void APlanChangeRetiresBeforeItCreates()
    {
        string host = Read(Upscaler);
        int ensure = host.IndexOf("public bool EnsureFeature(in UpscalePlan plan)", StringComparison.Ordinal);
        Assert.True(ensure > 0);
        string body = host.Substring(ensure, 1600);

        int matches = body.IndexOf("_feature.Matches(settings)", StringComparison.Ordinal);
        int retire = body.IndexOf("RetireFeature();", StringComparison.Ordinal);
        int create = body.IndexOf("_device.CreateDlssFeature(settings", StringComparison.Ordinal);
        Assert.True(matches > 0 && retire > matches && create > retire,
            "EnsureFeature must keep a matching feature, and otherwise retire before creating:\n" + body);
    }

    /// <summary>
    /// The device path allocates the motion attachment for either temporal
    /// consumer, exactly as the GL path does since this phase. The two bodies are
    /// separate by design (a device cannot route through four hundred lines of raw
    /// GL), which is precisely why a gate that moves on one side has to be pinned
    /// on the other.
    /// </summary>
    [Fact]
    public void TheDevicePathAllocatesTheMotionAttachmentForEitherConsumer()
    {
        string platform = VulkanPlatformSource.Read();
        Assert.Contains("bool temporalRequested = OptimumTemporalRequested;", platform);
        Assert.Contains(
            "DisableOptimumUpscaler(\"Primary motion attachment (device): \" + error.Message);", platform);

        // The history slots stay TAA's own on this path too.
        Assert.Contains("list[OptimumTaaHistoryIndexA] = CreateOptimumHistoryTarget(width, height);", platform);
        Assert.Contains("OptimumAdoptTaaTargets(list, taaRequested);", platform);
    }

    // ---- the one NGX lifetime and its owner ---------------------------------

    [Fact]
    public void TheShimPassesTheSecondArgumentShutdown1WritesThrough()
    {
        string shim = Read("native/optimum-ngx/optimum_ngx.c");

        Assert.Contains("typedef OptimumNgxResult (*pfn_shutdown1)(void *, int *);", shim);
        Assert.Contains("int remainingReferences = 0;", shim);
        Assert.Contains(
            "((pfn_shutdown1)g_ngx.shutdown1)(device, &remainingReferences)", shim);

        // The one-pointer typedef must not be what shutdown1 is called through any more.
        Assert.DoesNotContain("((pfn_handle)g_ngx.shutdown1)", shim);
    }

    /// <summary>
    /// One call site for the entry point that ends the lifetime, and it is the
    /// owner's. Anything else - a host, a platform, a test fixture - would be a
    /// second way to get the order wrong.
    /// </summary>
    [Fact]
    public void OnlyTheLifetimeOwnerCallsShutdown1()
    {
        var callers = new List<string>();
        foreach (string file in ManagedSources())
        {
            string text = File.ReadAllText(file);
            if (text.Contains("NgxInterop.Shutdown1(") || text.Contains("NgxShim.Shutdown("))
            {
                callers.Add(Path.GetFileName(file));
            }
        }

        // NgxInterop declares the entry point; NgxLifetime is the only thing that calls it.
        callers.Sort(StringComparer.Ordinal);
        Assert.Equal(new[] { "NgxInterop.cs", "NgxLifetime.cs" }, callers);
    }

    [Fact]
    public void TheOwnerRefusesASecondShutdownAndASecondBringUp()
    {
        string owner = Read("Optimum.Render.Vulkan/Upscale/Ngx/NgxLifetime.cs");

        Assert.Contains("if (_shutDown) return NgxLifetimeOutcome.AlreadyShutDown;", owner);
        Assert.Contains("if (_initialized) return NgxLifetimeOutcome.AlreadyInitialized;", owner);
        // A live feature after the release and the drain means NGX stays up: the
        // fatal pair is Shutdown1 followed by a release, never the other way round.
        Assert.Contains("return NgxLifetimeOutcome.FeatureStillLive;", owner);
    }

    /// <summary>
    /// The settings path - the tab switching the upscaler off or changing the preset,
    /// and the renderer standing the upscaler down at runtime - retires the feature
    /// and leaves NGX up. Nothing on it may shut NGX down: the process gets one
    /// lifetime, so the user would need a restart to get DLSS back.
    /// </summary>
    [Fact]
    public void ASettingsChangeRetiresTheFeatureAndNeverShutsNgxDown()
    {
        string platform = VulkanPlatformSource.Read();

        int apply = platform.IndexOf(
            "public override void ApplyOptimumUpscalerSettings()", StringComparison.Ordinal);
        Assert.True(apply > 0);
        string body = platform.Substring(apply, 700);

        Assert.Contains("upscaler.RetireFeature();", body);
        Assert.DoesNotContain("ShutDownUpscaler", body);
        Assert.DoesNotContain("upscaler.Shutdown", body);
        Assert.DoesNotContain("upscaler = null", body);

        // The host's own stand-down path is the same: a reason, not a shutdown.
        string host = Read(Upscaler);
        int fail = host.IndexOf("private bool Fail(string reason)", StringComparison.Ordinal);
        Assert.True(fail > 0);
        string failBody = host.Substring(fail);
        Assert.DoesNotContain("NgxLifetime.ShutDown", failBody);
    }

    /// <summary>
    /// ShutdownGraphics is the only caller of the teardown, and it can run twice -
    /// the fallback path calls it as well - so the second one must reach nothing.
    /// </summary>
    [Fact]
    public void OnlyTheGraphicsTeardownShutsTheUpscalerDown()
    {
        string platform = VulkanPlatformSource.Read();
        var sites = new List<int>();
        for (int at = 0; ; )
        {
            int found = platform.IndexOf("ShutDownUpscaler();", at, StringComparison.Ordinal);
            if (found < 0) break;
            sites.Add(found);
            at = found + 1;
        }
        Assert.Single(sites);

        int shutdownGraphics = platform.IndexOf(
            "public override void ShutdownGraphics()", StringComparison.Ordinal);
        Assert.True(shutdownGraphics > 0 && sites[0] > shutdownGraphics,
            "the only ShutDownUpscaler call must be inside ShutdownGraphics");

        // Twice through ShutdownGraphics is survivable because the host is dropped and
        // the owner refuses a second shutdown anyway.
        Assert.Contains("upscaler = null;", platform);
    }

    /// <summary>
    /// NGX is shut down because it was brought up, never because the upscaler still
    /// works.
    ///
    /// <c>Unavailable</c> is set by every later <c>Fail</c> - the realistic one being
    /// a driver that refuses <c>CreateFeature1</c> - while NGX is still initialised
    /// on a live VkDevice. Gating Shutdown1 on it meant the client ran on, and
    /// <c>ShutdownGraphics</c> then destroyed the device with NGX still up: the
    /// use-after-free this class documents, and no Shutdown1 to spend the one process
    /// lifetime on either.
    /// </summary>
    [Fact]
    public void TheNgxShutdownIsGatedOnInitialisationNotOnUsability()
    {
        string upscaler = Read(Upscaler);
        string shutdown = Between(upscaler, "public void Shutdown()", "public void Dispose()");

        // The gate now lives in the one owner, which knows whether NGX is up; the
        // host only says whether the lifetime is its own to end.
        Assert.DoesNotContain("Unavailable == null", shutdown);
        Assert.Contains("if (_ownsSession)", shutdown);
        Assert.Contains("NgxLifetime.ShutDown(", shutdown);

        string owner = Read("Optimum.Render.Vulkan/Upscale/Ngx/NgxLifetime.cs");
        string ownerShutdown = Between(owner, "internal NgxLifetimeOutcome ShutDown(", "internal static class NgxLifetime");
        Assert.Contains("if (!_initialized)", ownerShutdown);
        Assert.DoesNotContain("Unavailable", ownerShutdown);

        // AdoptSession owns no lifetime, so a test host that adopts retires and
        // drains but must never shut the owner's NGX down.
        string adopt = Between(upscaler, "internal bool AdoptSession(", "// ---------------------------------------------------------------- plan");
        Assert.DoesNotContain("NgxLifetime.ShutDown", adopt);
        Assert.Contains("_ownsSession = false;", adopt);
    }

    /// <summary>
    /// Preparing NGX's log and cache directory is best effort in both places that do
    /// it. The host's own preparation already swallowed the two exceptions; the
    /// session then retried the same call unguarded, and nothing between it and
    /// <c>InitializeGraphics</c> catches - so an unwritable data path fell the whole
    /// client back to OpenGL over a directory NGX only writes logs into.
    /// </summary>
    [Fact]
    public void TheNgxDataDirectoryNeverAbortsTheVulkanBringUp()
    {
        string session = Read(Session);
        string initialize = Between(session, "public NgxResult Initialize(", "// There is deliberately no Shutdown here.");

        Assert.Contains("Directory.CreateDirectory(ApplicationDataPath)", initialize);
        Assert.Contains("catch (IOException)", initialize);
        Assert.Contains("catch (UnauthorizedAccessException)", initialize);
        Assert.Contains("!string.IsNullOrEmpty(ApplicationDataPath)", initialize);
    }

    /// <summary>
    /// The teardown contract: a frame reserved by BeginFrame and never submitted is
    /// closed before the drain waits, and the drain collects through its value.
    /// Otherwise every resource retired inside that frame stays queued for ever -
    /// including the DLSS feature the drain exists to release before Shutdown1.
    /// </summary>
    [Fact]
    public void DrainRetirementsClosesAnAbandonedFrameAndCollectsThroughItsValue()
    {
        string ring = Read(Ring);
        string drain = Between(ring, "public int DrainRetirements()", "public ulong AbortFrame()");

        Assert.Contains("AbortFrame()", drain);
        Assert.Contains("CollectThrough(", drain);
        Assert.Contains("Math.Max(_timeline.FrameCompleted, abandoned)", drain);

        // The abort is a reset of the slot's pool, not a submit: an abandoned
        // recording may sit inside a rendering scope, which vkEndCommandBuffer would
        // reject, while vkResetCommandPool only refuses buffers in the pending state.
        string abandon = Between(ring, "public ulong AbandonFrame()", "/// <summary>\n    /// After <see cref=\"EndFrameAndSubmit\" />");
        Assert.Contains("ResetCommandPool", abandon);
        Assert.Contains("abandoned <= _timeline.FrameSignalled", abandon);

        // The device stops describing itself as in-frame once the drain abandoned it.
        string device = Read("Optimum.Render.Vulkan/VulkanDevice.Dlss.cs");
        string deviceDrain = Between(device, "internal int DrainDeferredDeletions()", "/// <summary>\n    /// Point-upscales");
        Assert.Contains("_frameActive = false;", deviceDrain);
    }

    /// <summary>
    /// Both GPU tests keep the host inside the teardown.
    ///
    /// A failed assertion between <c>BeginFrame</c> and <c>Present</c> would
    /// otherwise hand the next test on the shared fixture device an open frame and a
    /// live DLSS feature; the adopted-session <c>Shutdown</c> retires the feature and
    /// drains, and the drain abandons the unsubmitted frame.
    /// </summary>
    [Fact]
    public void TheGpuTestsShutTheHostDownFromAFinally()
    {
        string tests = Read(GpuTests);

        int hosts = 0;
        const string marker = "DlssUpscaler host = Host(";
        for (int at = tests.IndexOf(marker, StringComparison.Ordinal); at >= 0;
             at = tests.IndexOf(marker, at + marker.Length, StringComparison.Ordinal))
        {
            hosts++;
            // Bounded by the next Host(...) - to the end of the file it would let one
            // host's try/finally be satisfied by another member's, which is exactly the
            // teardown this test exists to find missing.
            int next = tests.IndexOf(marker, at + marker.Length, StringComparison.Ordinal);
            string after = next >= 0
                ? tests[(at + marker.Length)..next]
                : tests[(at + marker.Length)..];

            // The try that guards the host comes before anything that can fail.
            int tryAt = after.IndexOf("try", StringComparison.Ordinal);
            int planAt = after.IndexOf("host.TryPlan(", StringComparison.Ordinal);
            Assert.True(tryAt >= 0, "no try after a Host(...) at " + at);
            Assert.True(planAt < 0 || tryAt < planAt,
                "a planning assertion runs before the teardown is armed, after the Host(...) at " + at);

            // And the finally it opens shuts the host down.
            int finallyAt = after.IndexOf("finally", StringComparison.Ordinal);
            Assert.True(finallyAt > 0, "no finally after the Host(...) at " + at);
            int shutdownAt = after.IndexOf("host.Shutdown();", finallyAt, StringComparison.Ordinal);
            Assert.True(shutdownAt > 0, "the finally after the Host(...) at " + at + " does not shut the host down");
        }

        Assert.Equal(3, hosts);
    }

    /// <summary>
    /// Wave-2 review, 2026-09-12. The same rule, in the file the previous round did
    /// not read: the passthrough GPU suite builds its own hosts with
    /// <c>new DlssUpscaler(Log)</c> rather than through <c>Host(...)</c>, and one of
    /// them shut down only on the successful path. NGX's lifetime is process-wide, so
    /// a host leaked by a failed assertion here is not this test's failure - it is the
    /// next test on the shared device falling over.
    /// </summary>
    [Fact]
    public void ThePassthroughGpuTestsShutTheirHostsDownFromAFinally()
    {
        string tests = Read(PassthroughGpuTests);

        int hosts = 0;
        const string marker = "var host = new DlssUpscaler(";
        for (int at = tests.IndexOf(marker, StringComparison.Ordinal); at >= 0;
             at = tests.IndexOf(marker, at + marker.Length, StringComparison.Ordinal))
        {
            hosts++;
            int next = tests.IndexOf(marker, at + marker.Length, StringComparison.Ordinal);
            string after = next >= 0
                ? tests[(at + marker.Length)..next]
                : tests[(at + marker.Length)..];

            int finallyAt = after.IndexOf("finally", StringComparison.Ordinal);
            Assert.True(finallyAt > 0, "no finally after the host at " + at);
            int shutdownAt = after.IndexOf("host.Shutdown();", finallyAt, StringComparison.Ordinal);
            Assert.True(shutdownAt > 0,
                "the finally after the host at " + at + " does not shut the host down");
        }

        Assert.Equal(2, hosts);
    }

    // ---- the native shim the lifetime runs through --------------------------

    /// <summary>
    /// The Windows shim build creates its output directory and keeps the tail-call
    /// guard. Without the directory the link fails on a clean checkout and the
    /// fallback line blames a missing compiler; without the flag the shim may tail
    /// call into NGX, which then resolves the managed caller's module from the return
    /// address instead of the shim's and refuses - the whole reason the shim exists.
    /// </summary>
    [Fact]
    public void TheWindowsShimBuildCreatesItsOutputDirectoryAndKeepsTheTailCallGuard()
    {
        string project = Read(Project);
        string windows = Between(project, "<Exec Condition=\"'$(OS)' == 'Windows_NT'\"", "ContinueOnError");

        Assert.Contains("if not exist", windows);
        Assert.Contains("mkdir", windows);
        Assert.Contains("-fno-optimize-sibling-calls", windows);
        // Joined with a bare &amp;, never &amp;&amp;: cmd binds `if not exist X mkdir X &amp;&amp; cc`
        // as one conditional, so an existing directory would skip the compile too.
        Assert.DoesNotContain("mkdir &quot;$(NgxShimOut)&quot; &amp;&amp;", windows);

        // The same guard build.sh calls load-bearing, so the two hosts agree.
        string script = Read("native/optimum-ngx/build.sh");
        Assert.Contains("-fno-optimize-sibling-calls", script);
    }

    /// <summary>
    /// A failed shim compile ships nothing rather than the previous build.
    ///
    /// The <c>Exists()</c> condition that packages the shim cannot tell a fresh
    /// library from a stale one, and a native library older than the managed side
    /// that P/Invokes it is an ABI mismatch inside NGX - not the "DLSS unavailable"
    /// line this build is supposed to degrade to.
    /// </summary>
    [Fact]
    public void AFailedShimCompileNeverLeavesAStaleLibraryToPackage()
    {
        string windows = Between(Read(Project), "<Exec Condition=\"'$(OS)' == 'Windows_NT'\"", "ContinueOnError");
        Assert.Contains("OptimumNgx.tmp.dll", windows);
        Assert.Contains("move /y", windows);
        Assert.Contains("del /q", windows);
        // The final name is written by the move, never by the compiler.
        Assert.DoesNotContain("-o &quot;$(NgxShimOutWin)\\OptimumNgx.dll&quot;", windows);

        string script = Read("native/optimum-ngx/build.sh");
        Assert.Contains("-o \"$target.tmp\"", script);
        Assert.Contains("rm -f \"$target.tmp\" \"$target\"", script);
        Assert.Contains("mv -f \"$target.tmp\" \"$target\"", script);
    }

    /// <summary>
    /// No compiler ships nothing either - the wave-2 half of the same finding.
    ///
    /// A host that built the shim yesterday and lost its compiler today reaches the
    /// packaging condition with the same stale library a failed compile would have
    /// left, so both "no cc" branches delete the output instead of walking past it.
    /// The Windows branch is asserted as text (there is no cmd here to run it); the
    /// shell branch is driven for real below.
    /// </summary>
    [Fact]
    public void AHostWithNoCompilerAlsoLeavesNoStaleLibraryToPackage()
    {
        string windows = Between(Read(Project), "<Exec Condition=\"'$(OS)' == 'Windows_NT'\"", "ContinueOnError");
        // "where cc.exe" failing lands in the trailing || branch: it must delete, not just echo.
        string noCompiler = windows[windows.LastIndexOf("|| (", StringComparison.Ordinal)..];
        Assert.Contains("del /q", noCompiler);
        Assert.Contains("OptimumNgx.dll", noCompiler);
        Assert.Contains("the NGX shim was not built on this host", noCompiler);
    }

    /// <summary>
    /// The shell branch, driven: seed a stale library, run build.sh with a compiler
    /// name that does not exist, and require it gone and the build still green.
    /// </summary>
    [Fact]
    public void BuildShWithNoCompilerDeletesTheStaleLibrary()
    {
        string script = PatchReader.FindRepositoryFile("native/optimum-ngx/build.sh");
        string dir = Path.Combine(Path.GetTempPath(), "optimum-ngx-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string stale = Path.Combine(dir, RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "libOptimumNgx.dylib"
                : "libOptimumNgx.so");
            File.WriteAllText(stale, "stale");

            ProcessStartInfo start = new("bash", "\"" + script + "\" \"" + dir + "\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.Environment["CC"] = "optimum-ngx-no-such-compiler";
            using Process process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.Equal(0, process.ExitCode);
            Assert.Contains("no C compiler", output);
            Assert.False(File.Exists(stale), "build.sh left a stale " + stale + " for the csproj to package");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>Every C# file of the backend and its tests - the only places NGX is reachable from.</summary>
    private static IEnumerable<string> ManagedSources()
    {
        string upscale = Path.GetDirectoryName(PatchReader.FindRepositoryFile(
            "Optimum.Render.Vulkan/Upscale/DlssUpscaler.cs"))!;
        string backend = Path.GetFullPath(Path.Combine(upscale, ".."));
        string tests = Path.GetFullPath(Path.Combine(backend, "..", "Optimum.Render.Vulkan.Tests"));
        foreach (string directory in new[] { backend, tests })
        {
            if (!Directory.Exists(directory)) continue;
            foreach (string file in Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal) ||
                    file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                yield return file;
            }
        }
    }

    private static string Between(string text, string start, string end)
    {
        int from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "not found: " + start);
        int to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to >= 0, "not found after " + start + ": " + end);
        return text[from..to];
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
}
