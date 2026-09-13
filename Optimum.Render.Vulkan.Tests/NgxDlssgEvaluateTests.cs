using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// DLSS frame generation running for real on this GPU, from this renderer: the
/// sibling of <see cref="NgxDlssEvaluateTests" /> for <c>NVSDK_NGX_Feature_FrameGeneration</c>.
///
/// The synthetic scene is a horizontal sine band that moves 16 display pixels per
/// frame and moves back the next (frames alternate A, B, A, B...), with the motion
/// vectors saying exactly that at half resolution (8 render pixels, the DLSS SR
/// pairing the guide recommends: "this resource should be the same motion vector
/// resource that was provided to the upscaler"), a constant depth, and an opaque white
/// UI rectangle that does not move. Frames are evaluated with Present between them
/// and no readback inside the loop; both outputs are read back inside the last frame.
///
/// Everything skips, never fails, when the shim, the driver library or the feature
/// libraries are absent (see <see cref="NgxRuntime" />).
/// </summary>
[Collection(NgxCollection.Name)]
public class NgxDlssgEvaluateTests
{
    private readonly ITestOutputHelper _output;
    private readonly NgxRuntime _ngx;

    public NgxDlssgEvaluateTests(ITestOutputHelper output, NgxRuntime ngx)
    {
        _output = output;
        _ngx = ngx;
    }

    private const int DisplayWidth = 1280;
    private const int DisplayHeight = 720;
    private const int RenderWidth = 640;
    private const int RenderHeight = 360;

    /// <summary>Display pixels the band moves between frame A and frame B.</summary>
    private const int Shift = 16;

    /// <summary>The sine's period in display pixels: long enough that a half shift stays between the two frames.</summary>
    private const int Period = 256;

    /// <summary>The UI rectangle, in display pixels.</summary>
    private const int UiX = 96, UiY = 96, UiWidth = 320, UiHeight = 160;

    /// <summary>What both outputs are filled with before the first evaluate; a pixel still holding it was never written.</summary>
    private static readonly byte[] Sentinel = { 1, 2, 3, 4 };

    private const int Frames = 8;

    // ---------------------------------------------------------- the deliverable

    /// <summary>
    /// Create, eight frames, read back, release. Asserted: every evaluate returns
    /// Success; OutputReal is the last backbuffer (the guide: "the algorithm will just
    /// make a copy of pBackbuffer"); OutputInterpolated was written everywhere and,
    /// wherever the two real frames differ, lies between them; the layers - sync and
    /// best practices - stay clean; the release answers Success and nothing stays live.
    /// </summary>
    [SkippableFact]
    public void FrameGenerationEvaluatesAcrossFramesAndBothOutputsCarryTheirFrames()
    {
        VulkanDevice seam = Require(out int mark);
        Scene scene = Scene.Create(seam);
        int realDelta;
        Interpolation report;
        try
        {
        LogImages(seam, scene);

        RunResult run = Run(seam, scene, userInterfaceRecomposition: true);

        // OutputReal against the backbuffer it copied (frame B: the loop ends on an odd index).
        realDelta = MaxChannelDelta(run.Real, scene.BackbufferB, out int realDiffering);
        Log("OutputReal vs backbuffer B: max channel delta " + realDelta + ", pixels differing " + realDiffering +
            " of " + DisplayWidth * DisplayHeight);

        report = Measure(run.Interpolated, scene);
        Log(report.ToString());

        ReportValidation(seam, mark);
        Release(seam);
        }
        finally
        {
            scene.Release(seam);
        }

        Assert.True(realDelta <= 1, "OutputReal is not the backbuffer: max channel delta " + realDelta);
        Assert.True(report.SentinelFraction < 0.01,
            "OutputInterpolated was not written: " + report.SentinelFraction.ToString("P2") + " still hold the fill");
        Assert.True(report.DifferingPixels > 100_000, "the scene barely moved: " + report.DifferingPixels);
        Assert.True(report.BetweenFraction > 0.95,
            "OutputInterpolated leaves the range of its two real frames on " +
            (1 - report.BetweenFraction).ToString("P2") + " of the moving pixels");
        GpuTest.AssertCleanSince(seam, mark);
    }

    /// <summary>
    /// UI recomposition measured rather than assumed: the same frames through two
    /// feature instances, <c>DLSSG.UserInterfaceRecompositionEnabled</c> 1 then 0. The
    /// UI is present only in pUI and in the backbuffer - pHudless never has it. What is
    /// reported: pixels that differ between the two interpolated outputs inside and
    /// outside the UI rectangle, and how far each output's UI rectangle is from the UI.
    /// Asserted: both instances create and evaluate, the device really built a second
    /// feature for the second setting, and the run stays clean. What the switch does to
    /// the pixels is the number, not an assert: the header is the only documentation.
    /// </summary>
    [SkippableFact]
    public void UiRecompositionIsACreateTimeSwitchAndItsEffectIsMeasured()
    {
        VulkanDevice seam = Require(out int mark);
        Scene scene = Scene.Create(seam);
        try
        {
        int createdBefore = seam.FrameGenerationFeaturesCreated;
        RunResult on = Run(seam, scene, userInterfaceRecomposition: true);
        RunResult off = Run(seam, scene, userInterfaceRecomposition: false);
        Assert.Equal(createdBefore + 2, seam.FrameGenerationFeaturesCreated);
        Assert.False(seam.FrameGenerationSettings.UserInterfaceRecomposition);

        int insideDiffering = 0, outsideDiffering = 0, insideCount = 0;
        double onUiError = 0, offUiError = 0;
        for (int y = 0; y < DisplayHeight; y++)
        {
            for (int x = 0; x < DisplayWidth; x++)
            {
                int o = (y * DisplayWidth + x) * 4;
                int delta = 0;
                for (int c = 0; c < 3; c++) delta = Math.Max(delta, Math.Abs(on.Interpolated[o + c] - off.Interpolated[o + c]));
                bool inside = InUi(x, y);
                if (inside)
                {
                    insideCount++;
                    if (delta > 2) insideDiffering++;
                    for (int c = 0; c < 3; c++)
                    {
                        onUiError += 255 - on.Interpolated[o + c];
                        offUiError += 255 - off.Interpolated[o + c];
                    }
                }
                else if (delta > 2)
                {
                    outsideDiffering++;
                }
            }
        }
        Log("UI recomposition 1 vs 0, interpolated pixels differing by more than 2: inside the UI " +
            insideDiffering + " of " + insideCount + ", outside " + outsideDiffering + " of " +
            (DisplayWidth * DisplayHeight - insideCount));
        Log("mean distance from the UI's white inside the rectangle: uir=1 " +
            (onUiError / (insideCount * 3)).ToString("0.###") + ", uir=0 " +
            (offUiError / (insideCount * 3)).ToString("0.###") + " (of 255)");
        Log("uir=1 " + Measure(on.Interpolated, scene));
        Log("uir=0 " + Measure(off.Interpolated, scene));

        ReportValidation(seam, mark);
        Release(seam);
        }
        finally
        {
            scene.Release(seam);
        }
        GpuTest.AssertCleanSince(seam, mark);
    }

    /// <summary>
    /// A changed backbuffer size is a new feature (the guide: "you must release the
    /// existing DLSS-FG feature and re-create it"), and the old one goes onto the frame
    /// timeline rather than being released under a frame that may still name it.
    /// Asserted: an output pair whose size does not match the backbuffer is refused
    /// with FAIL_InvalidParameter before NGX is called (no feature created for it);
    /// the second size creates exactly one more feature and retires exactly one; the
    /// retired feature's release answers Success once the timeline is drained; the new
    /// feature's outputs are the new size and carry their frames; created and retired
    /// agree at the end, which is "a resize does not leak features".
    /// </summary>
    [SkippableFact]
    public void AResizeRebuildsTheFrameGenerationFeatureWithoutLeakingIt()
    {
        VulkanDevice seam = Require(out int mark);
        const int smallWidth = 960, smallHeight = 540;
        Scene big = Scene.Create(seam);
        Scene small = Scene.Create(seam, smallWidth, smallHeight);
        try
        {
            int created = seam.FrameGenerationFeaturesCreated;
            int retired = seam.FrameGenerationFeaturesRetired;

            Run(seam, big, userInterfaceRecomposition: true);
            NgxDlssgFeature? before = seam.FrameGenerationFeatureForTests;
            Assert.NotNull(before);
            Assert.Equal(created + 1, seam.FrameGenerationFeaturesCreated);

            // The size check runs before any NGX call: a mismatched pair is refused,
            // not handed to the driver, and the live feature is left alone.
            seam.BeginFrame();
            NgxResult refused = seam.EvaluateFrameGeneration(
                new FrameGenerationImages(big.B, big.Depth, big.MotionB, big.HudlessB, big.Ui,
                    small.OutputInterpolated, small.OutputReal),
                new NgxDlssgEvaluation());
            seam.Present();
            Log("1280x720 backbuffer into a 960x540 output pair: " + NgxInterop.Describe(refused));
            Assert.Equal(NgxResult.FailInvalidParameter, refused);
            Assert.Same(before, seam.FrameGenerationFeatureForTests);
            Assert.Equal(created + 1, seam.FrameGenerationFeaturesCreated);

            RunResult run = Run(seam, small, userInterfaceRecomposition: true);
            Assert.Equal(created + 2, seam.FrameGenerationFeaturesCreated);
            Assert.Equal(retired + 1, seam.FrameGenerationFeaturesRetired);
            Assert.Equal((uint)smallWidth, seam.FrameGenerationSettings.DisplayWidth);
            Assert.Equal((uint)smallHeight, seam.FrameGenerationSettings.DisplayHeight);

            seam.DrainDeferredDeletions();
            Log("released the pre-resize feature: " + NgxInterop.Describe(before!.LastReleaseResult) +
                ", parameters: " + NgxInterop.Describe(before.LastDestroyParametersResult));
            Assert.False(before.IsValid);
            Assert.Equal(NgxResult.Success, before.LastReleaseResult);
            Assert.Equal(NgxResult.Success, before.LastDestroyParametersResult);

            Assert.Equal(smallWidth * smallHeight * 4, run.Real.Length);
            int realDelta = MaxChannelDelta(run.Real, small.BackbufferB, out int realDiffering);
            int unwritten = 0;
            for (int i = 0; i < run.Interpolated.Length; i += 4)
            {
                if (run.Interpolated[i] == Sentinel[0] && run.Interpolated[i + 1] == Sentinel[1] &&
                    run.Interpolated[i + 2] == Sentinel[2] && run.Interpolated[i + 3] == Sentinel[3])
                {
                    unwritten++;
                }
            }
            Log("after the resize: OutputReal vs backbuffer B max channel delta " + realDelta + " (" + realDiffering +
                " pixels differ); OutputInterpolated pixels still holding the fill " + unwritten + " of " +
                smallWidth * smallHeight);
            Assert.True(realDelta <= 1, "OutputReal is not the resized backbuffer: max channel delta " + realDelta);
            Assert.True(unwritten < smallWidth * smallHeight / 100, "OutputInterpolated was not written: " + unwritten);

            ReportValidation(seam, mark);
            Release(seam);
            Assert.Equal(seam.FrameGenerationFeaturesCreated - created, seam.FrameGenerationFeaturesRetired - retired);
        }
        finally
        {
            big.Release(seam);
            small.Release(seam);
        }
        GpuTest.AssertCleanSince(seam, mark);
    }

    /// <summary>
    /// <see cref="NgxDlssgEvaluation.MatrixFromGl" /> is a copy, not a transpose: a
    /// column-major float[16] for column vectors and NGX's row-major post-multiplied
    /// float[4][4] are the same 16 numbers in the same memory order. Pinned on 16
    /// distinct values, so a transpose cannot pass: GL's element 14 (column 3, row 2,
    /// where a GL projection keeps -2fn/(f-n)) must land in the 15th float of the
    /// Matrix4x4, M43.
    /// </summary>
    [Fact]
    public void AGlMatrixReachesNgxInTheSameMemoryOrder()
    {
        var gl = new float[16];
        for (int i = 0; i < 16; i++) gl[i] = i + 1;
        Matrix4x4 m = NgxDlssgEvaluation.MatrixFromGl(gl);
        ReadOnlySpan<float> memory = MemoryMarshal.CreateReadOnlySpan(ref m.M11, 16);
        for (int i = 0; i < 16; i++) Assert.Equal(gl[i], memory[i]);
        Assert.Equal(15f, m.M43);
        Assert.Throws<ArgumentException>(() => NgxDlssgEvaluation.MatrixFromGl(new float[15]));
    }

    // ------------------------------------------------------------- the loop

    private readonly record struct RunResult(byte[] Interpolated, byte[] Real);

    /// <summary>
    /// <see cref="Frames" /> frames, Present between them, readback only inside the
    /// last one. Frame 0 is a reset: there is no previous frame to interpolate from.
    /// </summary>
    private RunResult Run(VulkanDevice seam, Scene scene, bool userInterfaceRecomposition)
    {
        byte[]? interpolated = null, real = null;
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 2.5f, (float)DisplayWidth / DisplayHeight, 0.1f, 1000f);
        Matrix4x4.Invert(projection, out Matrix4x4 inverse);

        bool frameOpen = false;
        try
        {
        for (int index = 0; index < Frames; index++)
        {
            bool frameB = index % 2 == 1;
            seam.BeginFrame();
            frameOpen = true;

            var images = new FrameGenerationImages(
                Backbuffer: frameB ? scene.B : scene.A,
                Depth: scene.Depth,
                MotionVectors: frameB ? scene.MotionB : scene.MotionA,
                Hudless: frameB ? scene.HudlessB : scene.HudlessA,
                Ui: scene.Ui,
                OutputInterpolated: scene.OutputInterpolated,
                OutputReal: scene.OutputReal);

            var frame = new NgxDlssgEvaluation
            {
                CameraViewToClip = projection,
                ClipToCameraView = inverse,
                // A still camera: all the motion is the band's own, and it is in the vectors.
                ClipToPrevClip = Matrix4x4.Identity,
                PrevClipToClip = Matrix4x4.Identity,
                CameraNear = 0.1f,
                CameraFar = 1000f,
                CameraFov = MathF.PI / 2.5f,
                CameraAspectRatio = (float)DisplayWidth / DisplayHeight,
                Reset = index == 0,
                BackbufferFrameId = (ulong)(index + 1),
            };

            NgxResult result = seam.EvaluateFrameGeneration(images, frame, userInterfaceRecomposition);
            if (index == 0 || result != NgxResult.Success)
            {
                Log("frame " + index + " uir=" + (userInterfaceRecomposition ? 1 : 0) +
                    " create: " + NgxInterop.Describe(seam.LastFrameGenerationCreateResult) +
                    " (Width " + DisplayWidth + ", Height " + DisplayHeight + ", DLSSG.BackbufferFormat " +
                    (uint)Format.R8G8B8A8Unorm + ", CreationNodeMask 1, VisibilityNodeMask 1, " +
                    "DLSSG.UserInterfaceRecompositionEnabled " + (userInterfaceRecomposition ? 1 : 0) +
                    "), evaluate: " + NgxInterop.Describe(result));
            }
            Assert.Equal(NgxResult.Success, result);

            // No readback inside the loop: the feature's history has to build on the
            // GPU across real frames.
            if (index == Frames - 1)
            {
                interpolated = seam.ReadBackLevel0ForTests(scene.OutputInterpolated);
                real = seam.ReadBackLevel0ForTests(scene.OutputReal);
            }
            seam.Present();
            frameOpen = false;
        }
        }
        catch
        {
            // A failed frame must not leave the shared device with an open frame or a
            // live feature: NgxRuntime's Shutdown1 would refuse, and every later NGX test
            // would inherit the wreck instead of reporting its own result.
            if (frameOpen || seam.FrameGenerationFeatureForTests != null)
            {
                seam.RetireFrameGeneration();
                seam.DrainDeferredDeletions();
            }
            throw;
        }
        return new RunResult(interpolated!, real!);
    }

    /// <summary>Retire, drain, and check the release - the owner's order, which NgxRuntime's shutdown then completes.</summary>
    private void Release(VulkanDevice seam)
    {
        NgxDlssgFeature? feature = seam.FrameGenerationFeatureForTests;
        Assert.NotNull(feature);
        int live = NgxLifetime.LiveFeatures;
        seam.RetireFrameGeneration();
        Assert.Equal(live - 1, NgxLifetime.LiveFeatures);
        int drained = seam.DrainDeferredDeletions();
        Log("ReleaseFeature: " + NgxInterop.Describe(feature!.LastReleaseResult) + ", DestroyParameters: " +
            NgxInterop.Describe(feature.LastDestroyParametersResult) + " (retire queue drained " + drained + ")");
        Assert.Equal(NgxResult.Success, feature.LastReleaseResult);
        Assert.Equal(NgxResult.Success, feature.LastDestroyParametersResult);
        Assert.False(feature.IsValid);
        Assert.Null(seam.FrameGenerationFeatureForTests);
    }

    // ------------------------------------------------------------ the scene

    /// <summary>
    /// The synthetic scene at one display size (render size half of it). Its images
    /// live on the fixture device, which every NGX test in the process shares, so a
    /// test releases them in a finally (<see cref="Release(VulkanDevice)" />): at
    /// 1280x720 the ten images are about 30 MiB that would otherwise stay allocated
    /// for the whole run.
    /// </summary>
    private sealed class Scene
    {
        public int Width, Height;
        public int A, B, HudlessA, HudlessB, Ui, Depth, MotionA, MotionB, OutputInterpolated, OutputReal;
        public byte[] BackbufferA = Array.Empty<byte>();
        public byte[] BackbufferB = Array.Empty<byte>();

        public static Scene Create(VulkanDevice seam, int width = DisplayWidth, int height = DisplayHeight)
        {
            int renderWidth = width / 2, renderHeight = height / 2;
            var scene = new Scene { Width = width, Height = height };
            byte[] hudlessA = Hudless(0, width, height), hudlessB = Hudless(Shift, width, height);
            scene.BackbufferA = WithUi(hudlessA, width);
            scene.BackbufferB = WithUi(hudlessB, width);
            scene.A = Upload(seam, width, height, Format.R8G8B8A8Unorm, false, scene.BackbufferA, 4);
            scene.B = Upload(seam, width, height, Format.R8G8B8A8Unorm, false, scene.BackbufferB, 4);
            scene.HudlessA = Upload(seam, width, height, Format.R8G8B8A8Unorm, false, hudlessA, 4);
            scene.HudlessB = Upload(seam, width, height, Format.R8G8B8A8Unorm, false, hudlessB, 4);
            scene.Ui = Upload(seam, width, height, Format.R8G8B8A8Unorm, false, UiImage(width, height), 4);

            var depth = new float[renderWidth * renderHeight];
            Array.Fill(depth, 0.5f);
            scene.Depth = Upload(seam, renderWidth, renderHeight, Format.R32Sfloat, false,
                MemoryMarshal.AsBytes<float>(depth).ToArray(), 4);

            // Current to previous, in render pixels (the guide's convention and §7.1's):
            // a pixel of B was 16 display pixels = 8 render pixels to the left in A, so its
            // vector is -8; a pixel of A was 8 render pixels to the right in B.
            scene.MotionB = Upload(seam, renderWidth, renderHeight, Format.R16G16Sfloat, false,
                Motion(-Shift / 2f, renderWidth, renderHeight), 4);
            scene.MotionA = Upload(seam, renderWidth, renderHeight, Format.R16G16Sfloat, false,
                Motion(Shift / 2f, renderWidth, renderHeight), 4);

            var fill = new byte[width * height * 4];
            for (int i = 0; i < fill.Length; i += 4) System.Buffer.BlockCopy(Sentinel, 0, fill, i, 4);
            scene.OutputInterpolated = Upload(seam, width, height, Format.R8G8B8A8Unorm, true, fill, 4);
            scene.OutputReal = Upload(seam, width, height, Format.R8G8B8A8Unorm, true, fill, 4);
            return scene;
        }

        /// <summary>Deletes every image; the destruction rides the frame timeline, so this is legal between frames.</summary>
        public void Release(VulkanDevice seam)
        {
            foreach (int texture in new[] { A, B, HudlessA, HudlessB, Ui, Depth, MotionA, MotionB, OutputInterpolated, OutputReal })
            {
                if (texture > 0) seam.DeleteTexture(texture);
            }
        }
    }

    /// <summary>Red is the moving sine band, green a static vertical ramp, blue red's complement.</summary>
    private static byte Band(int x, int offset) =>
        (byte)Math.Round(128 + 100 * Math.Sin(2 * Math.PI * (x - offset) / Period));

    private static byte[] Hudless(int offset, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            byte green = (byte)(y * 255 / (height - 1));
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                byte red = Band(x, offset);
                pixels[o] = red;
                pixels[o + 1] = green;
                pixels[o + 2] = (byte)(255 - red);
                pixels[o + 3] = 255;
            }
        }
        return pixels;
    }

    private static bool InUi(int x, int y) => x >= UiX && x < UiX + UiWidth && y >= UiY && y < UiY + UiHeight;

    /// <summary>Opaque white inside the rectangle, zero elsewhere: premultiplied by construction.</summary>
    private static byte[] UiImage(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int y = UiY; y < UiY + UiHeight; y++)
        {
            for (int x = UiX; x < UiX + UiWidth; x++) Array.Fill(pixels, (byte)255, (y * width + x) * 4, 4);
        }
        return pixels;
    }

    /// <summary>backbuffer = ui + (1 - alpha) * hudless, with alpha 1 inside the rectangle.</summary>
    private static byte[] WithUi(byte[] hudless, int width)
    {
        byte[] pixels = (byte[])hudless.Clone();
        for (int y = UiY; y < UiY + UiHeight; y++)
        {
            for (int x = UiX; x < UiX + UiWidth; x++) Array.Fill(pixels, (byte)255, (y * width + x) * 4, 4);
        }
        return pixels;
    }

    private static byte[] Motion(float x, int renderWidth, int renderHeight)
    {
        var halves = new Half[renderWidth * renderHeight * 2];
        for (int i = 0; i < halves.Length; i += 2)
        {
            halves[i] = (Half)x;
            halves[i + 1] = (Half)0f;
        }
        return MemoryMarshal.AsBytes<Half>(halves).ToArray();
    }

    private static unsafe int Upload(
        VulkanDevice seam, int width, int height, Format format, bool storage, byte[] pixels, int bytesPerPixel)
    {
        fixed (byte* data = pixels)
        {
            return seam.CreateUpscaleTexture(width, height, format, storage, (IntPtr)data, bytesPerPixel);
        }
    }

    // ------------------------------------------------------------ measuring

    private static int MaxChannelDelta(byte[] actual, byte[] expected, out int differing)
    {
        int max = 0;
        differing = 0;
        for (int i = 0; i < expected.Length; i += 4)
        {
            int delta = 0;
            for (int c = 0; c < 3; c++) delta = Math.Max(delta, Math.Abs(actual[i + c] - expected[i + c]));
            if (delta > 0) differing++;
            max = Math.Max(max, delta);
        }
        return max;
    }

    private readonly record struct Interpolation(
        double SentinelFraction, int DifferingPixels, double BetweenFraction,
        double MeanPosition, double ErrorToA, double ErrorToB, double ErrorToHalfway)
    {
        public override string ToString() =>
            "OutputInterpolated: never written " + SentinelFraction.ToString("P3") +
            "; pixels outside the UI where the real frames differ by more than 8: " + DifferingPixels +
            ", between them (tolerance 8): " + BetweenFraction.ToString("P3") +
            "; mean position from A (0) to B (1) where they differ by more than 32: " + MeanPosition.ToString("0.###") +
            "; mean abs red error vs A " + ErrorToA.ToString("0.##") + ", vs B " + ErrorToB.ToString("0.##") +
            ", vs the half-way band " + ErrorToHalfway.ToString("0.##");
    }

    /// <summary>
    /// The red channel against frame A (offset 0), frame B (offset 16) and the band at
    /// offset 8, which is where an interpolation half-way between them puts it.
    /// </summary>
    private static Interpolation Measure(byte[] interpolated, Scene scene)
    {
        int sentinel = 0, differing = 0, between = 0, positioned = 0, counted = 0;
        double position = 0, errorA = 0, errorB = 0, errorHalf = 0;
        for (int y = 0; y < DisplayHeight; y++)
        {
            for (int x = 0; x < DisplayWidth; x++)
            {
                int o = (y * DisplayWidth + x) * 4;
                if (interpolated[o] == Sentinel[0] && interpolated[o + 1] == Sentinel[1] &&
                    interpolated[o + 2] == Sentinel[2] && interpolated[o + 3] == Sentinel[3])
                {
                    sentinel++;
                }
                if (InUi(x, y)) continue;

                int a = scene.BackbufferA[o], b = scene.BackbufferB[o], v = interpolated[o];
                counted++;
                errorA += Math.Abs(v - a);
                errorB += Math.Abs(v - b);
                errorHalf += Math.Abs(v - Band(x, Shift / 2));
                if (Math.Abs(a - b) <= 8) continue;
                differing++;
                if (v >= Math.Min(a, b) - 8 && v <= Math.Max(a, b) + 8) between++;
                if (Math.Abs(a - b) > 32)
                {
                    position += (double)(v - a) / (b - a);
                    positioned++;
                }
            }
        }
        return new Interpolation(
            (double)sentinel / (DisplayWidth * DisplayHeight), differing,
            differing == 0 ? 0 : (double)between / differing,
            positioned == 0 ? 0 : position / positioned,
            errorA / Math.Max(1, counted), errorB / Math.Max(1, counted), errorHalf / Math.Max(1, counted));
    }

    // ---------------------------------------------------------------- harness

    /// <summary>The scene's VkImage handles, so a layer message naming one can be traced to its role.</summary>
    private void LogImages(VulkanDevice seam, Scene scene)
    {
        Log("images: backbuffer A 0x" + seam.TextureImageForTests(scene.A).ToString("x") +
            ", backbuffer B 0x" + seam.TextureImageForTests(scene.B).ToString("x") +
            ", hudless A 0x" + seam.TextureImageForTests(scene.HudlessA).ToString("x") +
            ", hudless B 0x" + seam.TextureImageForTests(scene.HudlessB).ToString("x") +
            ", ui 0x" + seam.TextureImageForTests(scene.Ui).ToString("x") +
            ", depth 0x" + seam.TextureImageForTests(scene.Depth).ToString("x") +
            ", motion A 0x" + seam.TextureImageForTests(scene.MotionA).ToString("x") +
            ", motion B 0x" + seam.TextureImageForTests(scene.MotionB).ToString("x") +
            ", interpolated 0x" + seam.TextureImageForTests(scene.OutputInterpolated).ToString("x") +
            ", real 0x" + seam.TextureImageForTests(scene.OutputReal).ToString("x"));
    }

    private VulkanDevice Require(out int mark)
    {
        _ngx.Require();
        foreach (string line in _ngx.Diagnostics) Log(line);
        mark = _ngx.MessageMark();
        return _ngx.Device;
    }

    private void ReportValidation(VulkanDevice seam, int mark)
    {
        List<string> all = GpuTest.MessagesOf(seam);
        Log("validation messages since this test began: " + Math.Max(0, all.Count - mark));
        for (int i = Math.Max(0, mark); i < all.Count; i++) Log("  " + all[i].Split('\n')[0].Trim());
    }

    /// <summary>Both to the test output and to stderr, for the reason <see cref="NgxDlssEvaluateTests" /> gives.</summary>
    private void Log(string line)
    {
        _output.WriteLine(line);
        Console.Error.WriteLine("[dlssg] " + line);
        Console.Error.Flush();
    }
}
