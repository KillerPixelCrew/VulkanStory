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

public sealed class Fsr3Tests(ITestOutputHelper log)
{
    [Fact]
    public void MissingQualityUsesTheDefaultPreset()
    {
        Assert.Equal(Fsr3Backend.QualityOf("quality"), Fsr3Backend.QualityOf(null));
    }

    [Fact]
    public void BridgeAbiMatchesTheNativeStructs()
    {
        Assert.Equal(24, Marshal.SizeOf<Fsr3Image>());
        Assert.Equal(136, Marshal.SizeOf<Fsr3Frame>());
        Assert.Equal(104, Marshal.OffsetOf<Fsr3Frame>(nameof(Fsr3Frame.JitterX)).ToInt32());
    }

    [SkippableFact]
    public unsafe void SignedVulkanRuntimeProducesPixelsAndRecreatesContexts()
    {
        using var backend = new Fsr3Backend(log.WriteLine);
        Skip.If(backend.Unavailable != null, backend.Unavailable);
        using VulkanDevice device = GpuTest.CreateDevice(log, d =>
        {
            var previous = d.ConfigureContextOptions;
            d.ConfigureContextOptions = options =>
            {
                previous?.Invoke(options);
                options.RequirementContributors.Add(backend.Requirements!);
            };
        });
        device.UpscalerHandles(out nint instance, out nint physical, out nint logical);
        Assert.True(backend.BringUp(device, instance, physical, logical));
        var textures = new List<int>();
        try
        {
            foreach (string preset in OptimumConfig.UpscalerQualityNames)
            {
                Assert.True(backend.TryPlan(1920, 1080, preset, out var queried), backend.Unavailable);
                Assert.True(queried.IsValid);
            }
            foreach ((string quality, int dw, int dh) in new[]
                { ("quality", 640, 384), ("performance", 640, 384), ("dlaa", 640, 384), ("quality", 800, 448) })
            {
                Assert.True(backend.TryPlan(dw, dh, quality, out var plan), backend.Unavailable);
                Assert.Equal(OptimumConfig.RecommendedUpscalerLodBias(plan.RenderWidth, plan.DisplayWidth), plan.LodBias, 5);
                int w = plan.RenderWidth, h = plan.RenderHeight;
                var colors = new Half[w * h * 4];
                var motion = new Half[w * h * 4];
                var depth = new float[w * h];
                Array.Fill(depth, 0.5f);
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int p = (y * w + x) * 4;
                    Half value = (Half)((x < w / 2) == (y < h / 2) ? 0.8f : 0.1f);
                    colors[p] = colors[p + 1] = colors[p + 2] = value;
                    colors[p + 3] = (Half)1;
                    motion[p + 2] = (Half)1;
                    motion[p + 3] = (Half)0.5f;
                }
                int Upload<T>(T[] pixels, Format format, int bpp) where T : unmanaged
                {
                    fixed (T* ptr = pixels)
                    {
                        int id = device.CreateUpscaleTexture(w, h, format, false, (nint)ptr, bpp);
                        textures.Add(id);
                        return id;
                    }
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
                ReadOnlySpan<Half> result = MemoryMarshal.Cast<byte, Half>(device.ReadBackLevel0ForTests(output));
                Assert.InRange((float)result[((dh / 4) * dw + dw / 4) * 4], 0.55f, 1f);
                Assert.InRange((float)result[((dh / 4) * dw + dw * 3 / 4) * 4], 0f, 0.35f);
                device.Present();
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
