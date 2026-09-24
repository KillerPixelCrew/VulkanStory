// Source: Optimum.Tests/client-platform-windows-vanilla-regions-tests.cs
namespace Optimum.Tests
{
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// Vulkan-native plan, Phase 1A: "OFF is vanilla" is a test. ClientPlatformWindows.cs may
/// differ from the decompiled vanilla source in <c>_ref/</c> only in the members listed here.
/// The comparison is per class member (fields, properties, methods, nested types), with
/// comment lines dropped and whitespace collapsed, and ignores member order. A member that
/// changes and is not listed fails, and so does a listed member that no longer differs, so
/// the list stays the exact set of Optimum-owned regions.
/// </summary>
public class ClientPlatformWindowsVanillaRegionsTests
{
    private const string VanillaSource = "_ref/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs";

    private static readonly string[] OwnedRegions =
    {
        // Members Optimum adds (injected by the patcher): the TAA/FSR state and GL halves, the
        // step 3-5 graphics virtuals' GL overrides, frame pacing, the parity dump.
        "ApplyOptimumMotionAccumulateBlendState", "ApplyOptimumMotionBlendState",
        "ApplyTransparentMergeBlendState", "ApplyTransparentPassBlendState",
        "BeginFinalCompositionDrawBuffers", "BeginMotionOnlyWrite", "BeginMotionWrite",
        "BeginOcclusionQuery", "BeginOitAccumulation", "BindCurrentFrameBuffer",
        "BindCurrentFrameBufferKeepViewport", "BindOitTextures", "BindProgramTexture2D",
        "BindProgramTextureCube", "BindSampler", "BindUBO", "ClearBoundFrameBuffer", "ClearDefaultDepth",
        "ClearFrameBufferPass", "ClearSsaoTarget", "ClearTextureRegion", "CreateOitTargets",
        "CreateOptimumHistoryTargetGl", "DeleteMeshHandle", "DeleteOcclusionQuery", "DeleteUBO",
        "DeleteVertexArrayHandles", "DisableOptimumFsr", "DisableOptimumTaa", "DisposeShaderProgram",
        "EnableMotionDrawBuffers", "EnableMotionOnlyDrawBuffers", "EndFrame", "EndMotionOnlyWrite",
        "EndMotionWrite", "EndOcclusionQuery", "EnsureOptimumDefaults", "EnsureOptimumTimerResolution",
        "GenOcclusionQuery", "GraphicsBackendName", "InstallOptimumMotionWriteHooks",
        "LoadTextureFromRgbaPointer", "MotionAttachmentIndex", "OptimumAdoptFrameBufferSettings",
        "OptimumAdoptTaaTargets", "OptimumBgFpsFocusDebounceMs", "OptimumBgMaxFps", "OptimumCloudReactive",
        "OptimumFinishDeviceFrameBufferSetup", "OptimumFsrBlitActive", "OptimumFsrFramebufferIndex",
        "OptimumGlR32f", "OptimumMotionWriteActive", "OptimumOnProcessExit", "OptimumParityDumpAttachment",
        "OptimumParityReadTextureGl", "OptimumParitySlotName", "OptimumRenderSsao", "OptimumRunParityDump",
        "OptimumRunPendingTaaShaderReload", "OptimumSpinIterations", "OptimumSpinTailMinProcessorCount",
        "OptimumSsaoKernel", "OptimumTaaHistoryIndexA", "OptimumTaaHistoryIndexB", "OptimumTaaRequested",
        "OptimumTaaSharpenIndex", "OptimumSceneNoHudIndex", "OptimumUiTargetIndex", "OptimumTimeBeginPeriod", "OptimumTimeEndPeriod",
        "OptimumUndershootPercent", "OptimumWindowClientSize", "OptimumYieldThresholdMs",
        "ProbeThickLineSupport",
        // Phase 3b: the post chain as one virtual per pass, and the keep-the-viewport bind.
        "OptimumPostAmbientOcclusion", "OptimumPostSceneTexture", "OptimumPostGlowTexture",
        "OptimumPostBloom", "OptimumPostGodRays", "OptimumPostLuma", "OptimumPostFinish",
        "OptimumBindKeepViewport", "OptimumTaaResolveDraw", "OptimumTaaSharpenDraw",
        "OptimumPostAmbientOcclusionTexture", "OptimumPostSsaoInScene", "OptimumPostSsaaLevel",
        // Phase 3b stage 1e-1g: the client state a native bloom, god-rays, Luma and
        // final-composition pass reads.
        "OptimumRenderBloom", "OptimumRenderGodRays", "OptimumRenderFxaa", "OptimumSsaaLevel",
        "OptimumAmbientOcclusionTexture", "OptimumSsaoInScene",
        "ReadDefaultFramebuffer", "ReadTextureForParity", "RenderOptimumSkyMotion",
        "RenderOptimumTaaResolve", "RenderOptimumTaaSharpen", "RestorePrimaryDrawBuffers",
        "RestoreWorldDrawBuffers", "SelectBackDrawBuffer", "SelectFsrDrawBuffer", "SetBlendEnabled",
        "SetDepthRange", "SetOptimumMotionAttachmentIndex", "SetProgramSamplerUnit", "SetSamplerLodBias",
        "SetTextureDepthCompare", "SetTextureLodBias", "SetUniform", "SetUniformArray1", "SetUniformArray2",
        "SetUniformArray3", "SetUniformArray4", "SetUniformMatrices", "SetUniformMatrices4x3",
        "SetUniformMatrix", "TaaHistory", "TaaResolvedThisFrame", "TaaTargetsReady",
        "TryGetOcclusionQueryResult", "UnbindUBO", "UpdateUBO", "UseShaderProgram",
        "_optimumFocusLostStopwatch", "_optimumSettingsInitialized", "_optimumTimerResolutionRaised",
        "_taaFrameParity", "_taaHistoryValid", "optimumFsrDisabled", "optimumMotionAttachmentIndex",
        "optimumMotionDrawBuffersOff", "optimumMotionDrawBuffersOn", "optimumMotionOnlyDrawBuffers",
        "optimumMotionWriteActive", "optimumParityDumpDone", "optimumParityWorldFrames",
        "optimumTaaDisabled", "optimumTaaResolvedThisFrame", "optimumTaaShaderReloadPending",
        "optimumTaaTargetsReady", "taaResolvedColorTexture", "taaResolvedGlowTexture",
        "optimumSsaoInScene", "ApplyOptimumSceneSsao",
        // Optimum AO: the platform's visibility texture composed through ApplyOptimumSceneSsao,
        // and the slots and headless writer of its opt-in debug outputs.
        "optimumAmbientOcclusionTexture", "OptimumAoWorkingSlot", "OptimumAoEdgesSlot", "OptimumAoDepthSlot",
        "OptimumAoOutputSlot", "OptimumAoOutputCount", "OptimumHeadlessWriteAmbientOcclusion",
        // Headless render harness: the per-frame hook, its own in-world frame counter,
        // the chat-command script dispatch, the presented-frame readback and the clean
        // close from the render thread.
        "OptimumHeadlessTick", "OptimumHeadlessRunCommands", "OptimumHeadlessRunCommand",
        "OptimumHeadlessCaptureFrame", "optimumHeadlessWorldFrames", "optimumHeadlessCommandsDone",
        "optimumHeadlessCaptureDone", "optimumHeadlessFramesWritten",
        "OptimumHeadlessExitIfDone", "optimumHeadlessExitRequested",
        // The headless harness runs silent: the mixer is created muted and every
        // attempt to restore the volume is answered with silence.
        "StartAudio", "MasterSoundLevel",

        // Vanilla members with an Optimum edit (the patcher transplant targets and the members
        // it virtualizes in place: base edits, FSR/TAA/post chain, frame pacing, mesh bulk copy),
        // some of which also carry the compile fix-ups below.
        "BlitPrimaryToDefault", "BuildMipMaps", "CheckFboStatus", "ClearFrameBuffer", "CompileShader",
        "CreateShaderProgram", "CreateUBO", "CurrentFrameBuffer", "CurrentFrameBufferKeepVw",
        "DisposeFrameBuffers", "GetGraphicsCardRenderer", "GlGetMaxTextureSize", "GlToggleBlend",
        "LoadFrameBuffer", "LogAndTestHardwareInfosStage2", "MergeTransparentRenderPass", "MouseGrabbed",
        "Mouse_WheelChanged", "RebuildFrameBuffers", "RenderFinalComposition", "RenderFullscreenTriangle",
        "RenderPostprocessingEffects", "SetupDefaultFrameBuffers", "Start", "UnloadFrameBuffer",
        "UpdateMesh", "UpdateSSBOMesh", "Window_Resize", "updateIndices", "updateVAO", "window_RenderFrame",
        // Upstream's GPU indirect draw submission (issue #75: RenderMesh's multi-draw form and its
        // injected indirect-buffer fields) and its texture upload changes, merged 2026-09-17.
        "RenderMesh", "_optimumSharedIndirectCommands", "_optimumSingleIndirectBufferId",
        "_optimumSingleIndirectBufferCapacity", "LoadIntoTexture", "LoadTexture",

        // Compile fix-ups only: decompiler artefacts the donor tree rewrites to build
        // (((T)(ref e)).X becomes e.X, explicit OpenTK qualification, int casts). Not
        // transplanted; the vanilla IL of these stays in the patched DLL.
        "CheckGlError", "CheckGlErrorAlways", "GlGetError", "LoadMouseCursor", "Mouse_ButtonDown",
        "Mouse_ButtonUp", "Mouse_Move", "Window_FileDrop", "game_KeyDown", "game_KeyPress", "game_KeyUp",
    };

    [Fact]
    public void ClientPlatformWindowsDiffersFromVanillaOnlyInTheOwnedRegions()
    {
        string? vanillaPath = TryFind(VanillaSource);
        bool forkSnapshot = vanillaPath is null;
        vanillaPath ??= TryFind("build/snapshot/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        if (vanillaPath == null)
        {
            // _ref/ is the decompiled vanilla client, present wherever the lib is built.
            Assert.False(File.Exists(PatchReader.FindRepositoryFile(VulkanPlatformSource.ClientPlatformWindowsSource)),
                "build/ is materialised but _ref/ is not; the vanilla comparison cannot run");
            return;
        }

        List<Member> vanilla = ClassMembers(File.ReadAllText(vanillaPath), "ClientPlatformWindows");
        List<Member> patched = ClassMembers(VulkanPlatformSource.ReadClientPlatformWindows(), "ClientPlatformWindows");
        // Vanilla 1.22.7 splits into 262 members; far fewer means the splitter lost the class body.
        Assert.True(vanilla.Count > 200, "the vanilla member split found only " + vanilla.Count + " members");

        var differing = new SortedSet<string>(StringComparer.Ordinal);
        CollectUnmatched(patched, vanilla, differing);
        CollectUnmatched(vanilla, patched, differing);

        var owned = new SortedSet<string>(OwnedRegions, StringComparer.Ordinal);
        var unexpected = new SortedSet<string>(differing, StringComparer.Ordinal);
        unexpected.ExceptWith(owned);
        var stale = new SortedSet<string>(owned, StringComparer.Ordinal);
        stale.ExceptWith(differing);

        Assert.True(unexpected.Count == 0,
            "ClientPlatformWindows differs from vanilla outside the owned regions:\n" + string.Join("\n", unexpected));
        // The fork snapshot already contains some upstream edits; only the raw decompile
        // can prove that every listed member differs from vanilla.
        if (!forkSnapshot)
            Assert.True(stale.Count == 0,
                "listed as owned but identical to vanilla (remove from the list):\n" + string.Join("\n", stale));
    }

    private static void CollectUnmatched(List<Member> members, List<Member> against, SortedSet<string> into)
    {
        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Member member in against)
        {
            remaining.TryGetValue(member.Text, out int count);
            remaining[member.Text] = count + 1;
        }
        foreach (Member member in members)
        {
            if (remaining.TryGetValue(member.Text, out int count) && count > 0)
            {
                remaining[member.Text] = count - 1;
            }
            else
            {
                into.Add(member.Name);
            }
        }
    }

    private readonly record struct Member(string Name, string Text);

    /// <summary>
    /// Splits the body of the first class named <paramref name="className" /> into its
    /// brace-depth-1 members. String and character literals are skipped so braces inside
    /// them do not count; a closing brace followed by <c>;</c>, <c>,</c>, <c>)</c> or
    /// <c>.</c> continues the member (initialisers).
    /// </summary>
    private static List<Member> ClassMembers(string source, string className)
    {
        var lines = new StringBuilder();
        foreach (string line in source.Split('\n'))
        {
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            lines.Append(line).Append('\n');
        }
        string text = lines.ToString();

        Match declaration = Regex.Match(text, @"\bclass\s+" + className + @"\b");
        Assert.True(declaration.Success, "class " + className + " not found");
        int open = text.IndexOf('{', declaration.Index);
        var members = new List<Member>();
        int depth = 1;
        int start = open + 1;
        for (int i = open + 1; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                bool verbatim = i > 0 && (text[i - 1] == '@' || (text[i - 1] == '$' && i > 1 && text[i - 2] == '@'));
                i++;
                while (i < text.Length)
                {
                    if (verbatim && text[i] == '"' && i + 1 < text.Length && text[i + 1] == '"') { i += 2; continue; }
                    if (!verbatim && text[i] == '\\') { i += 2; continue; }
                    if (text[i] == '"') break;
                    i++;
                }
                continue;
            }
            if (c == '\'')
            {
                i++;
                while (i < text.Length && text[i] != '\'')
                {
                    if (text[i] == '\\') i++;
                    i++;
                }
                continue;
            }
            if (c == '{')
            {
                depth++;
                continue;
            }
            if (c == '}')
            {
                depth--;
                if (depth == 0) break;
                if (depth == 1)
                {
                    int next = i + 1;
                    while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
                    if (next < text.Length && ";,).".IndexOf(text[next]) >= 0) continue;
                    AddMember(members, text.Substring(start, i - start + 1));
                    start = i + 1;
                }
                continue;
            }
            if (c == ';' && depth == 1)
            {
                AddMember(members, text.Substring(start, i - start + 1));
                start = i + 1;
            }
        }
        return members;
    }

    private static void AddMember(List<Member> members, string raw)
    {
        string normalized = Regex.Replace(raw, @"\s+", " ").Trim();
        if (normalized.Length == 0 || normalized == ";") return;
        string header = Regex.Replace(normalized, @"^(\[[^\]]*\]\s*)+", string.Empty);
        int cut = header.Length;
        foreach (char stop in new[] { '(', '{', '=', ';' })
        {
            int index = header.IndexOf(stop);
            if (index >= 0 && index < cut) cut = index;
        }
        Match name = Regex.Match(header.Substring(0, cut), @"(\w+)\s*(<[^>]*>)?\s*$");
        members.Add(new Member(name.Success ? name.Groups[1].Value : header, normalized));
    }

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

// Source: Optimum.Tests/platform-device-branch-move-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// Vulkan-native plan, Phase 1A step 4: every OptimumRender.Device branch left
/// ClientPlatformWindows and lives in VulkanClientPlatform. The base keeps only the GL path
/// and calls platform virtuals where logic both backends share (post chain, TAA windows,
/// frame loop) meets the graphics API. An override whose base member is not virtual in the
/// patched lib would be bypassed silently; one the runtime self-check does not list would
/// fail mid-frame instead of falling back to OpenGL at install.
/// </summary>
public class PlatformDeviceBranchMoveCoverageTests
{
    private const string AbstractSource = "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs";

    [Fact]
    public void ClientPlatformWindowsHasNoDeviceBranch()
    {
        string code = StripComments(VulkanPlatformSource.ReadClientPlatformWindows());

        Assert.DoesNotContain("OptimumRender.Device", code);
        Assert.DoesNotContain("IOptimumGraphicsDevice", code);
        Assert.DoesNotContain("optimumDevice", code);
    }

    [Fact]
    public void EveryVulkanPlatformOverrideIsVirtualInThePatchedBaseAndSelfChecked()
    {
        string vulkan = StripComments(VulkanPlatformSource.Read());
        string abstractPlatform = StripComments(Read(AbstractSource));
        string windows = StripComments(VulkanPlatformSource.ReadClientPlatformWindows());
        string patcher = PatcherSource.Read();
        string injectedAbstract = Block(patcher, "[\"Vintagestory.Client.NoObf.ClientPlatformAbstract\"] = new()", "},");
        string virtualized = Block(patcher, "var methodsToVirtualize = new List<MethodTarget>", "};");
        string selfCheck = Block(Read(VulkanPlatformSource.MainFile), "internal static readonly ExpectedVirtual[] ExpectedVirtuals", "};");

        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(vulkan, @"public\s+override\s+(?:unsafe\s+)?[\w<>\[\].]+\s+(\w+)\s*(?:\(|$|\{)", RegexOptions.Multiline))
        {
            names.Add(match.Groups[1].Value);
        }
        Assert.True(names.Count > 90, "expected the whole graphics surface to be overridden, found " + names.Count);

        foreach (string name in names)
        {
            string member = @"\b" + Regex.Escape(name) + @"\s*(?:\(|\{|$|=>)";
            bool abstractMember = Regex.IsMatch(abstractPlatform, @"public\s+abstract\s+[^;{}=]*?" + member, RegexOptions.Multiline);
            bool injectedVirtual = Regex.IsMatch(abstractPlatform, @"public\s+virtual\s+[^;{}=]*?" + member, RegexOptions.Multiline);
            bool virtualizedInPlace = Regex.IsMatch(windows, @"public\s+virtual\s+[^;{}=]*?" + member, RegexOptions.Multiline);
            Assert.True(abstractMember || injectedVirtual || virtualizedInPlace,
                name + " is overridden by VulkanClientPlatform but is neither abstract nor virtual in the base");

            if (injectedVirtual)
            {
                Assert.True(injectedAbstract.Contains("\"" + name + "\",", StringComparison.Ordinal),
                    name + " is an injected ClientPlatformAbstract virtual the patcher does not inject");
                Assert.True(selfCheck.Contains("new(true, \"" + name + "\"", StringComparison.Ordinal)
                    || selfCheck.Contains("new(true, \"get_" + name + "\"", StringComparison.Ordinal),
                    name + " is missing from VulkanClientPlatform.ExpectedVirtuals");
            }
            if (virtualizedInPlace && !abstractMember && !injectedVirtual)
            {
                Assert.True(virtualized.Contains("\"" + name + "\"", StringComparison.Ordinal),
                    name + " is virtual in the donor ClientPlatformWindows but not in methodsToVirtualize");
                Assert.True(selfCheck.Contains("new(false, \"" + name + "\"", StringComparison.Ordinal),
                    name + " is missing from VulkanClientPlatform.ExpectedVirtuals");
            }
        }
    }

    [Fact]
    public void TheThreeBaseEditsAreInPlace()
    {
        string windows = VulkanPlatformSource.ReadClientPlatformWindows();
        string patcher = PatcherSource.Read();

        string frame = Body(windows, "private void window_RenderFrame(FrameEventArgs e)");
        int begin = frame.IndexOf("BeginFrame();", StringComparison.Ordinal);
        int handler = frame.IndexOf("frameHandler.OnNewFrame(dt);", StringComparison.Ordinal);
        int end = frame.IndexOf("EndFrame();", StringComparison.Ordinal);
        Assert.True(begin >= 0 && handler > begin && end > handler);
        Assert.Contains("((GameWindow)window).SwapBuffers();", Body(windows, "public override void EndFrame()"));

        Assert.Contains("SupportsThickLines = ProbeThickLineSupport();", Body(windows, "public void Start()"));
        Assert.Contains("GL.LineWidth(1.5f);", Body(windows, "public override bool ProbeThickLineSupport()"));

        string resize = Body(windows, "private void Window_Resize()");
        int notify = resize.IndexOf("OnWindowSizeChanged(((NativeWindow)window).ClientSize.X, ((NativeWindow)window).ClientSize.Y);", StringComparison.Ordinal);
        int rebuild = resize.IndexOf("RebuildFrameBuffers();", StringComparison.Ordinal);
        Assert.True(notify >= 0 && rebuild > notify, "the window size has to reach the platform before the rebuild");

        foreach (string target in new[] { "\"window_RenderFrame\", 1)", "\"Start\", 0)", "\"Window_Resize\", 0)" })
        {
            Assert.Contains("new(\"Vintagestory.Client.NoObf.ClientPlatformWindows\", " + target, patcher);
        }

        string abstractPlatform = Read(AbstractSource);
        Assert.Equal("{ }", Regex.Replace(Body(abstractPlatform, "public virtual void BeginFrame()"), @"\s+", " ").Trim());
        Assert.Equal("{ }", Regex.Replace(Body(abstractPlatform, "public virtual void OnWindowSizeChanged(int width, int height)"), @"\s+", " ").Trim());
    }

    [Fact]
    public void TheInjectedPlatformStateAccessorsAreShippedAndSelfChecked()
    {
        string patcher = PatcherSource.Read();
        string injectedWindows = Block(patcher, "[\"Vintagestory.Client.NoObf.ClientPlatformWindows\"] = new()", "},");
        string selfCheck = Block(Read(VulkanPlatformSource.MainFile), "internal static readonly string[] ExpectedWindowsMembers", "};");
        string windows = VulkanPlatformSource.ReadClientPlatformWindows();

        foreach (Match match in Regex.Matches(selfCheck, "\"(\\w+)\","))
        {
            string name = match.Groups[1].Value;
            Assert.Contains("\"" + name + "\",", injectedWindows);
            Assert.Matches(new Regex(@"public\s+[\w<>\[\]]+\s+" + name + @"\b"), windows);
        }
    }

    private static string StripComments(string source) =>
        Regex.Replace(source, @"//[^\n]*", string.Empty);

    private static string Block(string source, string header, string terminator)
    {
        int start = source.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing: " + header);
        int end = source.IndexOf(terminator, start, StringComparison.Ordinal);
        Assert.True(end > start);
        return source.Substring(start, end - start);
    }

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

    private static string Read(string relativePath) =>
        System.IO.File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
}
}

// Source: Optimum.Tests/platform-seam-deletion-coverage-tests.cs
namespace Optimum.Tests
{
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// Vulkan-native plan, Phase 1A step 5: the last graphics-API leaf sites outside the platform
/// call ClientPlatformAbstract virtuals, and the static device seam (IOptimumGraphicsDevice,
/// OptimumRender.Device) is gone. Every leaf virtual carries the GL line the site issued in its
/// ClientPlatformWindows override and the device call in VulkanClientPlatform, is injected by
/// the patcher on both types and is in the runtime self-check.
/// </summary>
public class PlatformSeamDeletionCoverageTests
{
    private const string AbstractSource = "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs";

    private static readonly string[] SeamNames = { "OptimumRender.Device", "IOptimumGraphicsDevice" };

    /// <summary>The trees that ship into the game: the lib donor, the mod forks, the API fork and the contracts.</summary>
    private static readonly string[] ShippedTrees =
    {
        "build/VintagestoryLib",
        "VSEssentials",
        "VSSurvivalMod",
        "VSCreativeMod",
        "VintagestoryApi",
        "optimum-api-contracts",
        "sources",
        "patches",
    };

    [Fact]
    public void NoShippedSourceNamesTheDeletedSeam()
    {
        string root = RepositoryRoot();
        Assert.True(Directory.Exists(Path.Combine(root, "build", "VintagestoryLib")), "build/VintagestoryLib is not materialised");

        var offenders = new List<string>();
        int scanned = 0;
        foreach (string tree in ShippedTrees)
        {
            string directory = Path.Combine(root, tree);
            if (!Directory.Exists(directory)) continue;
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".cs", StringComparison.Ordinal) && !file.EndsWith(".patch", StringComparison.Ordinal)) continue;
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal)) continue;
                scanned++;
                string text = File.ReadAllText(file);
                foreach (string name in SeamNames)
                {
                    if (text.Contains(name, StringComparison.Ordinal)) offenders.Add(relative + ": " + name);
                }
            }
        }

        Assert.True(scanned > 1000, "expected to scan the shipped trees, scanned " + scanned + " files");
        Assert.True(offenders.Count == 0, "the deleted device seam is still named:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void TheContractsKeepOnlyTheBackendDecisionAndTheForkBridge()
    {
        string contracts = Read("VintagestoryApi/Client/optimum-render-device.cs");

        Assert.Contains("public static EnumRenderBackend ActiveBackend = EnumRenderBackend.OpenGL;", contracts);
        Assert.Contains("public static string FallbackReason;", contracts);
        Assert.Contains("public static bool IsVulkan => ActiveBackend == EnumRenderBackend.Vulkan;", contracts);
        Assert.Contains("public static bool NoGraphicsApiWindow;", contracts);
        Assert.Contains("public static void FallBackToOpenGL(string reason)", contracts);
        Assert.Contains("public abstract class OptimumForkGraphics", contracts);
        Assert.DoesNotContain("interface ", contracts);

        // GameWindowNative's pre-window clear keys on the window flag, not on a device.
        string window = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/GameWindowNative.cs");
        Assert.Contains("if (!OptimumRender.NoGraphicsApiWindow)", window);

        // The fork bridge is for the forks only: the lib never reaches for it, and the
        // Vulkan platform publishes it with its graphics and withdraws it before teardown.
        foreach (string file in Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "build", "VintagestoryLib"), "*.cs", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("OptimumForkGraphics", File.ReadAllText(file));
        }
        string vulkan = VulkanPlatformSource.Read();
        Assert.Contains("OptimumForkGraphics.Active = new VulkanForkGraphics(this, device);", vulkan);
        string shutdown = Body(vulkan, "public override void ShutdownGraphics()");
        Assert.True(shutdown.IndexOf("OptimumForkGraphics.Active = null;", StringComparison.Ordinal)
            < shutdown.IndexOf("device?.Dispose();", StringComparison.Ordinal));

        foreach (string fork in new[]
        {
            "VSEssentials/Systems/WorldMap/ChunkLayer/OptimumMapPageRenderer.cs",
            "VSEssentials/Systems/WorldMap/ChunkLayer/OptimumMapTextureArray.cs",
            "VSEssentials/Systems/Weather/Newclouds/CloudRendererVolumetric.cs",
            "VSEssentials/Systems/Weather/Newclouds/CloudRendererMap.cs",
            "VSSurvivalMod/Entity/Behavior/BehaviorHideWaterSurface.cs",
        })
        {
            Assert.Contains("OptimumForkGraphics.Active;", Read(fork));
        }
        Assert.Contains("if (OptimumRender.IsVulkan)", Read("VSEssentials/Systems/WorldMap/ChunkLayer/OptimumBc7Support.cs"));
    }

    /// <summary>Each former seam site calls the platform and no longer issues the GL call itself.</summary>
    [Theory]
    [InlineData("Vintagestory.Client/ScreenManager.cs", "Platform.ClearDefaultDepth(num);|Platform.SetDepthRange(0f, 20000f);", "GL.ClearBuffer|GL.DepthRange")]
    [InlineData("Vintagestory.Client.NoObf/ClientMain.cs", "Platform.SetDepthRange(0f, 20000f);|Platform.SetDepthRange(0f, 1f);", "GL.DepthRange")]
    [InlineData("Vintagestory.Client.NoObf/VAO.cs", "platform.DeleteVertexArrayHandles(this);", "GL.")]
    [InlineData("Vintagestory.Client.NoObf/ChunkRenderer.cs", "game.Platform.SetTextureLodBias(textureIds, bias);|game.Platform.BindSampler(8, 0);", "GL.BindSampler|(TextureParameterName)34049")]
    [InlineData("Vintagestory.Client.NoObf/ShaderRegistry.cs", "platform.SetSamplerLodBias(sampler, bias);", "GL.SamplerParameter")]
    [InlineData("Vintagestory.Client.NoObf/SystemRenderFrameBufferDebug.cs", "game.Platform.SetTextureDepthCompare(frameBufferRef.DepthTextureId, 0);|game.Platform.SetTextureDepthCompare(frameBufferRef.DepthTextureId, 34894);", "(TextureParameterName)34892|SetOptimumDepthCompare")]
    [InlineData("Vintagestory.Client.NoObf/SvgLoader.cs", "num = ScreenManager.Platform.LoadTextureFromRgbaPointer(textureWidth, textureHeight, (IntPtr)(nint)ptr);", "GL.")]
    [InlineData("Vintagestory.Client.NoObf/InventoryItemRenderer.cs", "game.Platform.ClearTextureRegion(task.TexPos.atlasTextureId, (int)num, (int)num2, size, size, clearPixels);", "GL.TexSubImage2D")]
    [InlineData("Vintagestory.Client.NoObf/ClientSystemStartup.cs", "if (game.Platform.GraphicsBackendName == \"OpenGL\" && GL.GetString((StringName)7937).Contains(\"Arc(TM)\")", "optimumRendererName")]
    [InlineData("Vintagestory.ClientNative/Screenshot.cs", "Vintagestory.Client.ScreenManager.Platform.ReadDefaultFramebuffer(0, 0, size.Width, size.Height, val.GetPixels());", "GL.ReadPixels")]
    [InlineData("Vintagestory.Client.NoObf/SystemRenderSunMoon.cs", "occlQueryId = game.Platform.GenOcclusionQuery();|platform.TryGetOcclusionQueryResult(occlQueryId, out num2)|platform.BeginOcclusionQuery(occlQueryId);|platform.EndOcclusionQuery(occlQueryId);|game.Platform.DeleteOcclusionQuery(occlQueryId);|platform.GlColorMask(false, false, false, false);|platform.GlColorMask(true, true, true, true);", "GL.GenQueries|GL.GetQueryObject|GL.BeginQuery|GL.EndQuery|GL.DeleteQuery|GL.ColorMask")]
    [InlineData("Vintagestory.Client.NoObf/SystemRenderOITLayers.cs", "ScreenManager.Platform.SetProgramSamplerUnit(program.ProgramId, \"OITaccumulation\", 7);|ScreenManager.Platform.BeginOitAccumulation(currentTransparentfb);|ScreenManager.Platform.CreateOitTargets(transparentfb, layers, out revealTextureId, out accumTextureId);|ScreenManager.Platform.BindOitTextures(revealTextureId, accumTextureId);|ScreenManager.Platform.GLDeleteTexture(accumTextureId);|platform.ApplyTransparentPassBlendState();", "GL.DrawBuffers|GL.BlendFunc|GL.ClearBuffer|GL.GenTexture|GL.Uniform1|GL.BindTexture|GL.DeleteTexture|SetOptimumOitSampling")]
    public void TheLeafSiteCallsThePlatform(string file, string calls, string forbidden)
    {
        string code = StripComments(Read("build/VintagestoryLib/" + file));
        foreach (string call in calls.Split('|'))
        {
            Assert.True(code.Contains(call, StringComparison.Ordinal), file + " does not call " + call);
        }
        foreach (string token in forbidden.Split('|'))
        {
            Assert.False(code.Contains(token, StringComparison.Ordinal), file + " still contains " + token);
        }
    }

    [Fact]
    public void TheSharedIndexBufferIsDeletedByThePlatform()
    {
        string body = Body(StripComments(Read(AbstractSource)), "public static void DisposeIndexBuffer()");
        Assert.Contains("platform.DeleteMeshHandle(singleIndexBufferId);", body);
        Assert.DoesNotContain("GL.", body);
    }

    /// <summary>
    /// Phase 1 review: VAO.Dispose is the single release point on both backends, as vanilla's
    /// DeleteMesh is only a Dispose. The Vulkan DeleteMesh override must not release the
    /// device mesh itself (a double free of a reusable id), and the Vulkan
    /// DeleteVertexArrayHandles must (MeshRef.Dispose is called directly everywhere).
    /// </summary>
    [Fact]
    public void AMeshIsReleasedOnlyThroughVaoDispose()
    {
        string vulkan = StripComments(VulkanPlatformSource.Read());
        string deleteMesh = Body(vulkan, "public override void DeleteMesh(MeshRef modelref)");
        Assert.Contains("((VAO)modelref).Dispose();", deleteMesh);
        Assert.DoesNotContain("device.DeleteMesh", deleteMesh);
        Assert.Contains("device.DeleteMesh(vao.VaoId);", Body(vulkan, "public override void DeleteVertexArrayHandles(VAO vao)"));

        string swapchain = Read("Optimum.Render.Vulkan/Present/Swapchain.cs");
        Assert.Contains("_retirement.Retire(old, SwapchainPolicy.RetireAfter(old.LastPresentValue));", swapchain);
    }

    [Fact]
    public void TheOitLayersKeepOnlyTheirFailurePathUnitReset()
    {
        string code = StripComments(Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderOITLayers.cs"));
        int glCalls = Regex.Matches(code, @"\bGL\.").Count;
        int unitResets = Regex.Matches(code, Regex.Escape("try { GL.ActiveTexture((TextureUnit)33984); } catch { }")).Count;
        Assert.Equal(unitResets, glCalls);
    }

    /// <summary>(member name, ClientPlatformWindows signature, GL line, VulkanClientPlatform line or null for a documented no-op).</summary>
    public static IEnumerable<object?[]> LeafVirtuals()
    {
        yield return new object?[] { "SetDepthRange", "public override void SetDepthRange(float near, float far)", "GL.DepthRange(near, far);", null };
        yield return new object?[] { "ClearDefaultDepth", "public override void ClearDefaultDepth(float depth)", "GL.ClearBuffer((ClearBuffer)6145, 0, ref depth);", "ClearTargetDepth(CurrentTargetId, Math.Clamp(depth, 0f, 1f));" };
        yield return new object?[] { "DeleteMeshHandle", "public override void DeleteMeshHandle(int bufferId)", "GL.DeleteBuffer(bufferId);", "device.DeleteMesh(bufferId);" };
        yield return new object?[] { "DeleteVertexArrayHandles", "public override void DeleteVertexArrayHandles(VAO vao)", "GL.DeleteVertexArray(vao.VaoId);", "device.DeleteMesh(vao.VaoId);" };
        yield return new object?[] { "SetTextureLodBias", "public override void SetTextureLodBias(int[] textureIds, float bias)", "GL.TexParameter((TextureTarget)3553, (TextureParameterName)34049, bias);", "device.SetTextureParameter(textureIds[k], OptimumGlConstants.TextureLodBias, bias);" };
        yield return new object?[] { "SetSamplerLodBias", "public override void SetSamplerLodBias(int samplerId, float bias)", "GL.SamplerParameter(samplerId, (SamplerParameterName)34049, bias);", "device.SetSamplerParameter(samplerId, OptimumGlConstants.TextureLodBias, bias);" };
        yield return new object?[] { "SetTextureDepthCompare", "public override void SetTextureDepthCompare(int textureId, int mode)", "GL.TexParameter((TextureTarget)3553, (TextureParameterName)34892, mode);", "device.SetTextureParameter(textureId, OptimumGlConstants.TextureCompareMode, mode);" };
        yield return new object?[] { "ClearTextureRegion", "public override void ClearTextureRegion(int textureId, int x, int y, int width, int height, int[] pixels)", "GL.TexSubImage2D<int>((TextureTarget)3553, 0, x, y, width, height, (PixelFormat)32993, (PixelType)5121, pixels);", "device.UploadTexture2D(textureId, 0, x, y, width, height, EnumTexturePixelFormat.Rgba, pin.AddrOfPinnedObject());" };
        yield return new object?[] { "LoadTextureFromRgbaPointer", "public override int LoadTextureFromRgbaPointer(int width, int height, IntPtr pixels)", "GL.TexImage2D((TextureTarget)3553, 0, (PixelInternalFormat)32856, width, height, 0, (PixelFormat)6408, (PixelType)5121, pixels);", "device.CreateTexture2DRaw(width, height, OptimumGlConstants.Rgba8, pixels, 4);" };
        yield return new object?[] { "SetProgramSamplerUnit", "public override void SetProgramSamplerUnit(int programId, string samplerName, int unit)", "GL.Uniform1(GL.GetUniformLocation(programId, samplerName), unit);", "device.SetSamplerUnit(programId, samplerName, unit);" };
        yield return new object?[] { "CreateOitTargets", "public override void CreateOitTargets(FrameBufferRef transparent, int layers, out int revealTexture, out int accumTexture)", "GL.FramebufferTextureLayer((FramebufferTarget)36160, (FramebufferAttachment)36069, accumTexture, 0, 2);", "device.AttachTexture(transparent.FboId, (EnumFramebufferAttachment)36069, accumTexture, 2);" };
        yield return new object?[] { "BeginOitAccumulation", "public override void BeginOitAccumulation(FrameBufferRef transparent)", "GL.ClearBuffer((ClearBuffer)6144, 5, array3);", "StateDrawBuffers(transparent.FboId, 0x3F);" };
        yield return new object?[] { "BindOitTextures", "public override void BindOitTextures(int revealTexture, int accumTexture)", "GL.BindTexture((TextureTarget)35866, accumTexture);", "stated.BindTexture(7, accumTexture);" };
        yield return new object?[] { "GenOcclusionQuery", "public override int GenOcclusionQuery()", "GL.GenQueries(1, out queryId);", "return device.CreateOcclusionQuery();" };
        yield return new object?[] { "BeginOcclusionQuery", "public override void BeginOcclusionQuery(int queryId)", "GL.BeginQuery((QueryTarget)35092, queryId);", "device.BeginOcclusionQuery(queryId);" };
        yield return new object?[] { "EndOcclusionQuery", "public override void EndOcclusionQuery(int queryId)", "GL.EndQuery((QueryTarget)35092);", "device.EndOcclusionQuery(queryId);" };
        yield return new object?[] { "TryGetOcclusionQueryResult", "public override bool TryGetOcclusionQueryResult(int queryId, out int samples)", "GL.GetQueryObject(queryId, (GetQueryObjectParam)34918, out samples);", "samples = device.GetQueryResult(queryId);" };
        yield return new object?[] { "DeleteOcclusionQuery", "public override void DeleteOcclusionQuery(int queryId)", "GL.DeleteQuery(queryId);", "device.DeleteQuery(queryId);" };
        yield return new object?[] { "ReadDefaultFramebuffer", "public override void ReadDefaultFramebuffer(int x, int y, int width, int height, IntPtr destination)", "GL.ReadPixels(x, y, width, height, (PixelFormat)32993, (PixelType)5121, destination);", "device.ReadFramebufferColor(CurrentTargetId, x, y, width, height, destination);" };
        yield return new object?[] { "GraphicsBackendName", "public override string GraphicsBackendName", "return \"OpenGL\";", "public override string GraphicsBackendName => device.BackendName;" };
    }

    [Theory]
    [MemberData(nameof(LeafVirtuals))]
    public void EveryLeafVirtualHasBothBodiesAndIsShippedAndSelfChecked(string name, string signature, string gl, string? vulkanLine)
    {
        string abstractPlatform = StripComments(Read(AbstractSource));
        Assert.Matches(new Regex(@"public\s+virtual\s+[\w<>\[\].]+\s+" + name + @"\b"), abstractPlatform);

        string windows = VulkanPlatformSource.ReadClientPlatformWindows();
        Assert.Contains(gl, Body(windows, signature));

        string vulkan = VulkanPlatformSource.Read();
        string boundary = char.IsLetterOrDigit(signature[signature.Length - 1]) ? @"\b" : string.Empty;
        Assert.Single(Regex.Matches(vulkan, Regex.Escape(signature) + boundary));
        if (vulkanLine != null)
        {
            Assert.Contains(vulkanLine, vulkan);
        }
        else
        {
            Assert.Equal("{ }", Regex.Replace(Body(vulkan, signature), @"\s+", " ").Trim());
        }

        string patcher = PatcherSource.Read();
        Assert.Contains("\"" + name + "\",", Block(patcher, "[\"Vintagestory.Client.NoObf.ClientPlatformAbstract\"] = new()", "},"));
        Assert.Contains("\"" + name + "\",", Block(patcher, "[\"Vintagestory.Client.NoObf.ClientPlatformWindows\"] = new()", "},"));

        string selfCheck = Block(Read(VulkanPlatformSource.MainFile), "internal static readonly ExpectedVirtual[] ExpectedVirtuals", "};");
        Assert.True(selfCheck.Contains("new(true, \"" + name + "\"", StringComparison.Ordinal)
            || selfCheck.Contains("new(true, \"get_" + name + "\"", StringComparison.Ordinal), name + " is not in the self-check");
    }

    [Fact]
    public void TheRemovedInjectedHelpersAreNoLongerShipped()
    {
        string patcher = PatcherSource.Read();
        Assert.DoesNotContain("\"SetOptimumDepthCompare\"", patcher);
        Assert.DoesNotContain("\"SetOptimumOitSampling\"", patcher);
    }

    private static string RepositoryRoot() =>
        Path.GetDirectoryName(PatchReader.FindRepositoryFile("VintageStory.slnx"))!;

    private static string StripComments(string source) =>
        Regex.Replace(source, @"//[^\n]*", string.Empty);

    private static string Block(string source, string header, string terminator)
    {
        int start = source.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing: " + header);
        int end = source.IndexOf(terminator, start, StringComparison.Ordinal);
        Assert.True(end > start);
        return source.Substring(start, end - start);
    }

    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing: " + signature);
        int cursor = start + signature.Length;
        while (cursor < source.Length && char.IsWhiteSpace(source[cursor])) cursor++;
        if (string.CompareOrdinal(source, cursor, "=>", 0, 2) == 0)
        {
            return source.Substring(cursor, source.IndexOf(';', cursor) - cursor + 1);
        }
        int open = source.IndexOf('{', cursor);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
        }
        throw new InvalidOperationException("unbalanced body: " + signature);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
}
}
