using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

public class XessTests(ITestOutputHelper log)
{
    [Fact]
    public void MissingQualityUsesTheDefaultPreset()
    {
        Assert.Equal(XessBackend.QualityOf("quality"), XessBackend.QualityOf(null));
    }

    [Fact]
    public void InitFlagsSelectAutoExposureForHdrInput()
    {
        // Bit assignments in the bundled XeSS xess.h, not DLSS or another SDK.
        Assert.Equal(1u << 8, XessBackend.InitFlags);
        Assert.Equal(0u, XessBackend.InitFlags & (1u << 6));
    }

    [Theory]
    [InlineData(1920, 1920, 0f)]
    [InlineData(960, 1920, -1f)]
    [InlineData(640, 1920, -1.5849625f)]
    public void MipBiasFollowsIntelInputToOutputRatio(int inputWidth, int outputWidth, float expected)
    {
        Assert.Equal(expected, XessBackend.RecommendedLodBias(inputWidth, outputWidth), 5);
    }

    [Fact]
    public void NativeAbiMatchesIntelHeaders()
    {
        Assert.Equal(48, Marshal.SizeOf<XessImage>());
        Assert.Equal(64, Marshal.SizeOf<XessInit>());
        Assert.Equal(360, Marshal.SizeOf<XessExecute>());
        Assert.Equal(288, Marshal.OffsetOf<XessExecute>(nameof(XessExecute.JitterX)).ToInt32());
        Assert.Equal(312, Marshal.OffsetOf<XessExecute>(nameof(XessExecute.ColorBase)).ToInt32());
    }

    [Fact]
    public unsafe void FailedDeviceFeatureNegotiationRestoresEveryExistingNode()
    {
        var optional = new PhysicalDeviceFaultFeaturesEXT
        {
            SType = StructureType.PhysicalDeviceFaultFeaturesExt,
            DeviceFault = true,
        };
        var v12 = new PhysicalDeviceVulkan12Features
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
            BufferDeviceAddress = true,
            PNext = &optional,
        };
        var root = new PhysicalDeviceFeatures2
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &v12,
        };
        void* chain = &root;
        int result = XessBackend.InvokeDeviceFeatureNegotiation(&chain, features =>
        {
            var changedRoot = (PhysicalDeviceFeatures2*)*features;
            var changed12 = (PhysicalDeviceVulkan12Features*)changedRoot->PNext;
            var changedOptional = (PhysicalDeviceFaultFeaturesEXT*)changed12->PNext;
            changedRoot->Features.ShaderInt16 = true;
            changed12->BufferDeviceAddress = false;
            changed12->PNext = null;
            changedOptional->DeviceFault = false;
            *features = null;
            return -1;
        });

        Assert.Equal(-1, result);
        Assert.Equal((nint)(&root), (nint)chain);
        Assert.False(root.Features.ShaderInt16);
        Assert.True(v12.BufferDeviceAddress);
        Assert.Equal((nint)(&v12), (nint)root.PNext);
        Assert.Equal((nint)(&optional), (nint)v12.PNext);
        Assert.True(optional.DeviceFault);
    }

    [Fact]
    public void FailedProviderDoesNotDisableAnotherProvider()
    {
        string previous = OptimumConfig.Upscaler;
        try
        {
            OptimumConfig.ResetUpscalerRuntimeDisabledForTests();
            OptimumConfig.Upscaler = "xess";
            Assert.True(OptimumConfig.DisableUpscalerAtRuntime());
            Assert.Equal("off", OptimumConfig.EffectiveUpscaler);
            OptimumConfig.Upscaler = "dlss";
            Assert.Equal("dlss", OptimumConfig.EffectiveUpscaler);
            Assert.False(OptimumConfig.UpscalerRuntimeDisabled);
            OptimumConfig.Upscaler = "xess";
            Assert.Equal("off", OptimumConfig.EffectiveUpscaler);
        }
        finally { OptimumConfig.ResetUpscalerRuntimeDisabledForTests(); OptimumConfig.Upscaler = previous; }
    }

    [Fact]
    public void InvalidMutableProviderDoesNotEnterTheTemporalUpscalerPath()
    {
        string previous = OptimumConfig.Upscaler;
        try
        {
            OptimumConfig.ResetUpscalerRuntimeDisabledForTests();
            OptimumConfig.Upscaler = null!;
            Assert.Equal("off", OptimumConfig.EffectiveUpscaler);
            Assert.False(OptimumConfig.UpscalerRuntimeDisabled);
            Assert.False(OptimumConfig.DisableUpscalerAtRuntime());
            OptimumConfig.Upscaler = "unrecognised";
            Assert.Equal("off", OptimumConfig.EffectiveUpscaler);
            OptimumConfig.Upscaler = " FSR3 ";
            Assert.Equal("fsr3", OptimumConfig.EffectiveUpscaler);
        }
        finally { OptimumConfig.ResetUpscalerRuntimeDisabledForTests(); OptimumConfig.Upscaler = previous; }
    }

    [SkippableFact]
    public unsafe void JitterKeepsStaticSceneRegistered()
    {
        using var backend = new XessBackend(log.WriteLine);
        Skip.If(backend.Requirements == null, backend.Unavailable);
        using var device = GpuTest.CreateDevice(log, d => {
            var previous = d.ConfigureContextOptions;
            d.ConfigureContextOptions = o => { previous?.Invoke(o); o.RequirementContributors.Add(backend.Requirements!); };
        });
        device.UpscalerHandles(out var instance, out var physical, out var logical);
        Skip.IfNot(backend.BringUp(device, instance, physical, logical), backend.Unavailable);
        Assert.True(backend.TryPlan(640, 384, "balanced", out var plan));
        int w = plan.RenderWidth, h = plan.RenderHeight, phases = OptimumTemporalMath.JitterPhaseCount(1f / plan.RenderScale);
        var resources = new List<int>();
        int Upload<T>(T[] data, Format format, int bpp) where T : unmanaged {
            fixed (T* ptr = data) { int id = device.CreateUpscaleTexture(w, h, format, false, (nint)ptr, bpp); resources.Add(id); return id; }
        }
        try {
            var depths = new float[w*h]; Array.Fill(depths, 0.5f);
            int depth = Upload(depths, Format.D32Sfloat, 4), motion = Upload(new Half[w*h*4], Format.R16G16B16A16Sfloat, 8);
            double[] wobble = new double[4];
            for (int convention = 0; convention < 4; convention++) {
                backend.RetireFeature();
                var temporal = new OptimumTemporalFrame();
                var frames = new OptimumTemporalFrame[phases];
                var inputs = new int[phases]; var outputs = new int[phases];
                for (int phase = 0; phase < phases; phase++) {
                    temporal.Advance(16.667f, w, h, plan.RenderScale, 0.1f, 1000f, 1f, null); temporal.JitterActive = true;
                    var colors = new Half[w*h*4];
                    for (int y=0; y<h; y++) for (int x=0; x<w; x++) {
                        int p = (y*w+x)*4;
                        double value = 0.5 + 0.2*Math.Sin(2*Math.PI*(x+0.5-temporal.JitterPx.X)/plan.RenderScale/32)
                            + 0.2*Math.Sin(2*Math.PI*(y+0.5-temporal.JitterPx.Y)/plan.RenderScale/32);
                        colors[p] = colors[p+1] = colors[p+2] = (Half)value; colors[p+3] = (Half)1;
                    }
                    inputs[phase] = Upload(colors, Format.R16G16B16A16Sfloat, 8);
                    outputs[phase] = device.CreateUpscaleTexture(640,384,Format.R16G16B16A16Sfloat,true); resources.Add(outputs[phase]);
                    frames[phase] = new OptimumTemporalFrame();
                    frames[phase].JitterPx.X = temporal.JitterPx.X * ((convention&1)==0 ? 1 : -1);
                    frames[phase].JitterPx.Y = temporal.JitterPx.Y * ((convention&2)==0 ? 1 : -1);
                }
                for (int frame=0; frame<phases*3; frame++) {
                    int phase=frame%phases; device.BeginFrame();
                    Assert.True(backend.Evaluate(plan,new UpscalerFrame(inputs[phase],depth,motion,outputs[phase],frames[phase]),out var error),error);
                    device.Present();
                }
                double minX=double.MaxValue,maxX=double.MinValue,minY=double.MaxValue,maxY=double.MinValue;
                device.BeginFrame();
                foreach(int texture in outputs) {
                    var pixels=MemoryMarshal.Cast<byte,Half>(device.ReadBackLevel0ForTests(texture));
                    double sx=0,cx=0,sy=0,cy=0;
                    for(int y=32;y<352;y++) for(int x=32;x<608;x++) {
                        double v=(float)pixels[(y*640+x)*4];
                        sx+=v*Math.Sin(2*Math.PI*x/32); cx+=v*Math.Cos(2*Math.PI*x/32);
                        sy+=v*Math.Sin(2*Math.PI*y/32); cy+=v*Math.Cos(2*Math.PI*y/32);
                    }
                    double px=Math.Atan2(cx,sx)*32/(2*Math.PI)*plan.RenderScale;
                    double py=Math.Atan2(cy,sy)*32/(2*Math.PI)*plan.RenderScale;
                    minX=Math.Min(minX,px); maxX=Math.Max(maxX,px); minY=Math.Min(minY,py); maxY=Math.Max(maxY,py);
                }
                device.Present();
                wobble[convention]=maxX-minX+maxY-minY;
                log.WriteLine($"convention {convention}: X={maxX-minX:F5}, Y={maxY-minY:F5} render pixels");
            }
            Assert.True(wobble[0] < 0.1, $"Shipped jitter wobble {wobble[0]}");
            Assert.True(wobble[0] * 2 < Math.Min(wobble[1], Math.Min(wobble[2],wobble[3])));
        } finally {
            backend.Shutdown(); foreach(int texture in resources) device.DeleteTexture(texture); device.DrainDeferredDeletions();
        }
        GpuTest.AssertClean(device);
    }

    [SkippableFact]
    public unsafe void RuntimeReconstructsAndRecreatesContextsWithValidation()
    {
        using var backend = new XessBackend(log.WriteLine);
        Skip.If(backend.Requirements == null, backend.Unavailable);
        using VulkanDevice device = GpuTest.CreateDevice(log, d =>
        {
            var previous = d.ConfigureContextOptions;
            d.ConfigureContextOptions = options => { previous?.Invoke(options); options.RequirementContributors.Add(backend.Requirements!); };
        });
        device.UpscalerHandles(out nint instance, out nint physical, out nint logical);
        Skip.IfNot(backend.BringUp(device, instance, physical, logical), backend.Unavailable);
        var textures = new List<int>();
        try
        {
            foreach (string preset in OptimumConfig.XessQualityNames)
            {
                Assert.True(backend.TryPlan(1920, 1080, preset, out var queried), backend.Unavailable);
                Assert.True(queried.IsValid);
                if (preset == "dlaa") Assert.Equal(1920, queried.RenderWidth);
            }
            foreach (var setting in new[] { ("quality", 640, 384), ("performance", 640, 384), ("dlaa", 640, 384), ("quality", 800, 448) })
            {
                (string quality, int dw, int dh) = setting;
                Assert.True(backend.TryPlan(dw, dh, quality, out var plan), backend.Unavailable);
                Assert.Equal(XessBackend.RecommendedLodBias(plan.RenderWidth, plan.DisplayWidth), plan.LodBias, 5);
                int w = plan.RenderWidth, h = plan.RenderHeight;
                var colors = new Half[w * h * 4];
                var motion = new Half[w * h * 4];
                var depth = new float[w * h];
                Array.Fill(depth, 0.5f);
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                {
                    int p = (y * w + x) * 4;
                    Half value = (Half)((x < w / 2) == (y < h / 2) ? 0.8f : 0.1f);
                    colors[p] = colors[p + 1] = colors[p + 2] = value; colors[p + 3] = (Half)1;
                    motion[p + 2] = (Half)1; motion[p + 3] = (Half)0.5f;
                }
                int Upload<T>(T[] data, Format format, int bpp) where T : unmanaged
                {
                    fixed (T* ptr = data) { int id = device.CreateUpscaleTexture(w, h, format, false, (nint)ptr, bpp); textures.Add(id); return id; }
                }
                int color = Upload(colors, Format.R16G16B16A16Sfloat, 8);
                int mv = Upload(motion, Format.R16G16B16A16Sfloat, 8);
                int z = Upload(depth, Format.D32Sfloat, 4);
                int output = device.CreateUpscaleTexture(dw, dh, Format.R16G16B16A16Sfloat, true);
                textures.Add(output);
                var temporal = new OptimumTemporalFrame();
                for (int i = 0; i < 5; i++)
                {
                    temporal.Advance(16.667f, w, h, plan.RenderScale, 0.1f, 1000f, 1f, null);
                    device.BeginFrame();
                    Assert.True(backend.Evaluate(plan, new UpscalerFrame(color, z, mv, output, temporal), out string? error), error);
                    device.Present();
                }
                device.BeginFrame();
                byte[] bytes = device.ReadBackLevel0ForTests(output);
                ReadOnlySpan<Half> result = MemoryMarshal.Cast<byte, Half>(bytes);
                Assert.InRange((float)result[((dh / 4) * dw + dw / 4) * 4], 0.6f, 1f);
                Assert.InRange((float)result[((dh / 4) * dw + dw * 3 / 4) * 4], 0f, 0.3f);
                device.Present();
                // Retire while the last frame is still in flight, as live switching does.
                backend.RetireFeature();
            }
        }
        finally
        {
            backend.Shutdown();
            foreach (int id in textures) device.DeleteTexture(id);
            device.DrainDeferredDeletions();
        }
        GpuTest.AssertClean(device);
    }
}
