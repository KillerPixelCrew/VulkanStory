using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// DLSS-FG design, step 2: the HUD-less scene snapshot - the guide's <c>pHudless</c>.
///
/// The snapshot's whole value is its position in the frame. The real stage order is
/// <c>RenderFinalComposition</c> -&gt; <c>RenderAfterFinalComposition</c> -&gt;
/// <c>BlitPrimaryToDefault</c> -&gt; Ortho (the 2D GUI): the composited image holds the
/// scene and nothing else for exactly the length of the first arrow, because the second
/// stage draws selection boxes and work-item guides onto that same image. A later change
/// that moves the capture one statement later, or one stage later, produces a snapshot that
/// looks right in every screenshot and is wrong for frame generation, so the position is
/// pinned here against the shipped source rather than described in a comment.
///
/// What the copy really does to the pixels is <c>SceneNoHudSnapshotTests</c>, on the GPU,
/// through the platform.
/// </summary>
public class SceneNoHudCoverageTests
{
    private const string PlatformSource =
        "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs";

    /// <summary>
    /// The capture is the last statement of the composition, after the draw-buffer restore
    /// and before the method returns - which is before ScreenManager calls the next stage.
    /// </summary>
    [Fact]
    public void TheCaptureIsTheLastThingTheCompositionDoes()
    {
        string platform = Platform();
        string body = MethodBody(platform, "public override void RenderFinalComposition()");

        Assert.Contains("OptimumCaptureSceneNoHud();", body);
        int capture = body.IndexOf("OptimumCaptureSceneNoHud();", StringComparison.Ordinal);
        int restore = body.LastIndexOf("RestoreWorldDrawBuffers(RenderSSAO);", StringComparison.Ordinal);
        Assert.True(restore >= 0 && capture > restore,
            "the capture must come after the draw-buffer restore, so it sees the finished composition");
        // Nothing draws between the capture and the end of the method.
        string tail = body.Substring(capture + "OptimumCaptureSceneNoHud();".Length);
        Assert.DoesNotContain("RenderFullscreenTriangle", tail);
        Assert.DoesNotContain(".Use();", tail);

        // And the stage the snapshot has to beat is still the next call in the frame, so
        // this remains the only moment that can be captured.
        string screenManager = Read("build/VintagestoryLib/Vintagestory.Client/ScreenManager.cs");
        int composition = screenManager.IndexOf("Platform.RenderFinalComposition();", StringComparison.Ordinal);
        int overlays = screenManager.IndexOf("CurrentScreen.RenderAfterFinalComposition(dt);", StringComparison.Ordinal);
        Assert.True(composition >= 0 && overlays > composition,
            "RenderAfterFinalComposition no longer follows RenderFinalComposition; the snapshot's position must be re-derived");
    }

    /// <summary>
    /// The slot is published the way <c>MotionAttachmentIndex</c> is - a backing field that
    /// starts at -1, a read-only property, and one publisher both framebuffer setups call -
    /// and the capture asks that index before it touches anything.
    /// </summary>
    [Fact]
    public void TheSlotIsPublishedAsAnIndexThatStartsUnset()
    {
        string platform = Platform();

        Assert.Contains("private const int OptimumSceneNoHudIndex = 23;", platform);
        // No initializer - Cecil injects the field but not the constructor, so it is 0 at
        // runtime; SetOptimumSceneNoHudIndex(-1) is what actually unpublishes the slot.
        Assert.Contains("private int optimumSceneNoHudIndex;", platform);
        Assert.Contains("SetOptimumSceneNoHudIndex(-1);", platform);
        Assert.Contains("public int SceneNoHudFrameBufferIndex", platform);
        Assert.Contains("public void SetOptimumSceneNoHudIndex(int index)", platform);

        string capture = MethodBody(platform, "public void OptimumCaptureSceneNoHud()");
        Assert.Contains("optimumSceneNoHudCaptured = false;", capture);
        Assert.Contains("if (optimumSceneNoHudIndex < 0 || frameBuffers == null) return;", capture);
        Assert.Contains("FrameBufferRef composite = OptimumCompositeFrameBuffer;", capture);
        Assert.Contains("optimumSceneNoHudCaptured = CopyOptimumSceneNoHud(composite, snapshot);", capture);
    }

    /// <summary>
    /// Skipped entirely when nothing wants it: one gate, read by both framebuffer setups,
    /// and nothing is allocated and nothing is copied when it is false.
    /// </summary>
    [Fact]
    public void NothingIsAllocatedWhenNothingWantsTheSnapshot()
    {
        string platform = Platform();
        string gate = MethodBody(platform, "public bool OptimumSceneNoHudRequested");
        // An upscaler, or frame generation (whose pHudless this is). With the frame
        // generation setting off EffectiveFrameGeneration is false, so the gate answers
        // exactly the upscaler's question it answered before the setting existed.
        Assert.Contains(
            "return Vintagestory.API.Config.OptimumConfig.UpscalerReplacesTaa || Vintagestory.API.Config.OptimumConfig.EffectiveFrameGeneration;",
            gate);
        // The effective value, never the raw setting: a stood-down or non-DLSS session
        // must not allocate frame generation's inputs.
        Assert.DoesNotContain("OptimumConfig.FrameGeneration;", gate);

        // The GL allocation and the device one are both behind that one question.
        Assert.Contains("if (OptimumSceneNoHudRequested)", platform);
        Assert.Contains("SetOptimumSceneNoHudIndex(-1);", platform);
        Assert.Contains("if (OptimumSceneNoHudRequested)", VulkanPlatformSource.Read());

        // The display size, on both paths: the snapshot is the composited image, and the
        // composited image is display-resolution whether or not an upscaler produced it.
        Assert.Contains("setupAttachment(optimumSceneNoHud, displayWidth, displayHeight, 0, val, (PixelInternalFormat)32856);", platform);
        Assert.Contains("CreateOptimumSceneNoHudTarget(displayWidth, displayHeight)", VulkanPlatformSource.Read());
    }

    /// <summary>
    /// Every new or changed lib member is a Cecil target; a member missing from the patcher
    /// compiles here and is absent from the shipped DLL.
    /// </summary>
    [Fact]
    public void EveryNewMemberIsListedForTheTransplant()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");
        foreach (string member in new[]
        {
            "OptimumSceneNoHudIndex", "SceneNoHudFrameBufferIndex", "optimumSceneNoHudIndex",
            "OptimumSceneNoHudRequested", "OptimumSceneNoHudFrameBuffer", "OptimumSceneNoHudCaptured",
            "optimumSceneNoHudCaptured", "SetOptimumSceneNoHudIndex", "OptimumCaptureSceneNoHud",
            "CopyOptimumSceneNoHud",
        })
        {
            Assert.Contains("\"" + member + "\"", patcher);
        }
        // RenderFinalComposition is already a transplant target (the capture is inside it).
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"RenderFinalComposition\", 0", patcher);
    }

    /// <summary>
    /// The device half: the copy is the platform's override, one blit, and its target is
    /// not a transient - the snapshot outlives the pass that wrote it, and with frame
    /// generation it outlives the frame.
    /// </summary>
    [Fact]
    public void TheDeviceCopyIsABlitIntoATargetThatIsNotTransient()
    {
        string vulkan = VulkanPlatformSource.Read();

        Assert.Contains("public override bool CopyOptimumSceneNoHud(FrameBufferRef source, FrameBufferRef destination)", vulkan);
        Assert.Contains("device.BlitColorScaled(", vulkan);
        Assert.Contains("private FrameBufferRef CreateOptimumSceneNoHudTarget(int width, int height)", vulkan);
        string target = MethodBody(vulkan, "private FrameBufferRef CreateOptimumSceneNoHudTarget(int width, int height)");
        Assert.Contains("device.CreateTexture2D(width, height,", target);
        Assert.DoesNotContain("CreateTransientTexture2D", target);

        // And the override is declared to the substitution check, or the routing test that
        // enumerates the platform's overrides fails on it.
        Assert.Contains("new(false, \"CopyOptimumSceneNoHud\", new[] { \"FrameBufferRef\", \"FrameBufferRef\" }),", vulkan);
    }

    private static string Platform() => Read(PlatformSource);

    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "no such member: " + signature);
        int open = source.IndexOf('{', start);
        Assert.True(open > start);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return source.Substring(open, i - open + 1);
            }
        }
        throw new InvalidOperationException("unterminated member: " + signature);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
}
