using System.Numerics;
using Silk.NET.Vulkan;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;
using VulkanStory.Render.Vulkan.Shaders;

namespace VulkanStory.Render.Vulkan.RayTracing;

/// <summary>Owns traced colour, coherent guides and frame-slot descriptors; the existing texture manager owns image retirement.</summary>
internal sealed unsafe class RayTracingRenderer : IDisposable
{
    internal const int Colour = 0, NormalRoughness = 1, DiffuseAlbedo = 2, SpecularAlbedo = 3,
        Depth = 4, Motion = 5, SpecularDistance = 6, DiffuseSignal = 7, SpecularSignal = 8, StableRadiance = 9, Glow = 10,
        ViewDepth = 11, DeviceDepth = 12;
    private const int ImageCount = 12, UniformFloats = 92;
    private sealed class Arena : IDisposable
    {
        private readonly VulkanContext context;
        internal DescriptorPool Pool;
        internal ulong Frame = ulong.MaxValue;
        internal Arena(VulkanContext context)
        {
            this.context = context;
            DescriptorPoolSize* sizes = stackalloc DescriptorPoolSize[4]
            {
                new(DescriptorType.AccelerationStructureKhr, 32), new(DescriptorType.UniformBuffer, 32),
                new(DescriptorType.StorageBuffer, 32), new(DescriptorType.StorageImage, 32 * ImageCount),
            };
            var info = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 32, PoolSizeCount = 4, PPoolSizes = sizes,
            };
            VulkanResult.Check(context.Api.CreateDescriptorPool(context.Device, &info, null, out Pool), "path tracing descriptor pool");
        }
        internal DescriptorSet Allocate(DescriptorSetLayout layout, ulong frame)
        {
            if (Frame != frame)
            {
                VulkanResult.Check(context.Api.ResetDescriptorPool(context.Device, Pool, 0), "recycling completed ray descriptor slot");
                Frame = frame;
            }
            var info = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = Pool,
                DescriptorSetCount = 1, PSetLayouts = &layout,
            };
            DescriptorSet set;
            VulkanResult.Check(context.Api.AllocateDescriptorSets(context.Device, &info, &set), "path tracing descriptor set");
            return set;
        }
        public void Dispose() => context.Api.DestroyDescriptorPool(context.Device, Pool, null);
    }

    private readonly VulkanContext context;
    private readonly FrameRing frames;
    private readonly TextureManager textures;
    private readonly BindlessTextureTable bindless;
    private readonly RayTracingPipeline pipeline;
    private readonly Arena[] arenas;
    private readonly Action<int> releaseTexture;
    private readonly int[] images = new int[ImageCount];
    private int deviceDepth;
    private VulkanBuffer? depthTransfer;
    private int width, height;
    private bool disposed;
    internal int Texture(int kind) => kind == DeviceDepth ? deviceDepth : images[kind];
    internal int Width => width;
    internal int Height => height;

    internal RayTracingRenderer(VulkanContext context, FrameRing frames, TextureManager textures,
        BindlessTextureTable bindless, ShaderCompiler compiler, Action<int> releaseTexture)
    {
        this.context = context; this.frames = frames; this.textures = textures; this.bindless = bindless;
        this.releaseTexture = releaseTexture;
        pipeline = new RayTracingPipeline(context, compiler, bindless.Layout);
        arenas = new Arena[frames.FramesInFlight];
        try { for (int i = 0; i < arenas.Length; i++) arenas[i] = new Arena(context); }
        catch
        {
            foreach (Arena? arena in arenas) arena?.Dispose();
            pipeline.Dispose();
            throw;
        }
    }

    private void EnsureImages(int nextWidth, int nextHeight)
    {
        if (nextWidth == width && nextHeight == height) return;
        // Retire before allocating so the allocator can use completed space;
        // recorded users keep their original images through the frame timeline.
        foreach (int image in images) if (image != 0) releaseTexture(image);
        if (deviceDepth != 0) releaseTexture(deviceDepth);
        deviceDepth = 0;
        if (depthTransfer != null) frames.DeferDeletion(depthTransfer);
        depthTransfer = null;
        Array.Clear(images); width = height = 0;
        try
        {
            for (int i = 0; i < ImageCount; i++)
            {
                Format format = i switch
                {
                    Colour or DiffuseSignal or SpecularSignal or StableRadiance => Format.R32G32B32A32Sfloat,
                    Depth or SpecularDistance or Glow or ViewDepth => Format.R32Sfloat,
                    _ => Format.R16G16B16A16Sfloat,
                };
                int id = textures.CreateStorage((uint)nextWidth, (uint)nextHeight, format);
                images[i] = id;
                if (textures.Get(id)!.Format != format)
                    throw new NotSupportedException("Path tracing storage format is unavailable: " + format);
            }
            deviceDepth = textures.Create((uint)nextWidth, (uint)nextHeight, Format.D32Sfloat);
            depthTransfer = new VulkanBuffer(context, checked((ulong)nextWidth * (ulong)nextHeight * sizeof(float)),
                BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit);
            width = nextWidth; height = nextHeight;
        }
        catch
        {
            foreach (int image in images) if (image != 0) releaseTexture(image);
            if (deviceDepth != 0) releaseTexture(deviceDepth);
            deviceDepth = 0;
            if (depthTransfer != null) frames.DeferDeletion(depthTransfer);
            depthTransfer = null;
            Array.Clear(images);
            throw;
        }
    }

    internal bool Draw(CommandBuffer commands, RayTracingScene scene, in RayTracingFrame frame,
        int nextWidth, int nextHeight, ulong frameIdentity, BarrierBatcher barriers)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!frame.HasMatrices || nextWidth <= 0 || nextHeight <= 0 || scene.LastInstanceCount == 0) return false;
        EnsureImages(nextWidth, nextHeight);
        if (!frames.Current.TryAllocateUniforms(UniformFloats * sizeof(float), out RingAllocation uniform)) return false;
        var values = new Span<float>((void*)uniform.Pointer, UniformFloats);
        values.Clear();
        frame.InverseProjection.Span.CopyTo(values.Slice(0, 16));
        frame.CameraToWorld.Span.CopyTo(values.Slice(16, 16));
        frame.PreviousWorldToClip.Span.CopyTo(values.Slice(32, 16));
        frame.CurrentWorldToClip.Span.CopyTo(values.Slice(48, 16));
        Put(values, 64, frame.OriginDelta, frame.FarPlane);
        values[68] = width; values[69] = height; values[70] = frame.Jitter.X; values[71] = frame.Jitter.Y;
        Put(values, 72, frame.SunDirection, frame.SunIntensity);
        Put(values, 76, frame.SunColour, 0);
        Put(values, 80, frame.SkyUpper, 0); Put(values, 84, frame.SkyHorizon, 0);
        var options = System.Runtime.InteropServices.MemoryMarshal.Cast<float, uint>(values.Slice(88, 4));
        options[0] = frame.FrameIdentity; options[1] = (uint)Math.Clamp(frame.Samples, 1, 64);
        options[2] = (uint)Math.Clamp(frame.Bounces, 1, 16); options[3] = frame.Reset ? 1u : 0u;

        DescriptorSet set = arenas[frames.Current.Index].Allocate(pipeline.FrameLayout, frameIdentity);
        var top = scene.TopLevel;
        var acceleration = new WriteDescriptorSetAccelerationStructureKHR
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
            AccelerationStructureCount = 1, PAccelerationStructures = &top,
        };
        DescriptorBufferInfo* buffers = stackalloc DescriptorBufferInfo[2]
        {
            new(uniform.Buffer, uniform.Offset, UniformFloats * sizeof(float)),
            new(scene.GeometryBuffer!.Handle, 0, scene.GeometryBuffer.Size),
        };
        DescriptorImageInfo* imageInfos = stackalloc DescriptorImageInfo[ImageCount];
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[ImageCount + 3];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, PNext = &acceleration,
            DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.AccelerationStructureKhr,
        };
        for (uint i = 0; i < 2; i++)
            writes[i + 1] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = i + 1,
                DescriptorCount = 1, DescriptorType = i == 0 ? DescriptorType.UniformBuffer : DescriptorType.StorageBuffer,
                PBufferInfo = &buffers[i],
            };
        for (int i = 0; i < ImageCount; i++)
        {
            VulkanTexture texture = textures.Get(images[i])!;
            textures.Require(barriers, commands, texture, ResourceUsage.StorageWriteRayTracing);
            imageInfos[i] = new DescriptorImageInfo(default, texture.View, ImageLayout.General);
            writes[i + 3] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = (uint)i + 3,
                DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = &imageInfos[i],
            };
        }
        barriers.Flush(commands);
        context.Api.UpdateDescriptorSets(context.Device, ImageCount + 3, writes, 0, null);
        pipeline.Trace(commands, set, bindless.Set, (uint)width, (uint)height);
        ExportDeviceDepth(commands, barriers);
        return true;
    }

    private void ExportDeviceDepth(CommandBuffer commands, BarrierBatcher barriers)
    {
        // Storage images cannot use D32. Copy the identical float encoding through
        // a buffer: image copies/blits between color and depth aspects are invalid.
        VulkanTexture source = textures.Get(images[Depth])!;
        VulkanTexture destination = textures.Get(deviceDepth)!;
        textures.Require(barriers, commands, source, ResourceUsage.TransferSrc);
        textures.Require(barriers, commands, destination, ResourceUsage.TransferDst);
        barriers.Flush(commands);
        RayAccelerationStructure.Barrier(context, commands, PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferReadBit, PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit);
        var copy = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D((uint)width, (uint)height, 1),
        };
        context.Api.CmdCopyImageToBuffer(commands, source.Image, ImageLayout.TransferSrcOptimal,
            depthTransfer!.Handle, 1, &copy);
        RayAccelerationStructure.Barrier(context, commands, PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit, PipelineStageFlags2.TransferBit, AccessFlags2.TransferReadBit);
        copy.ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.DepthBit, 0, 0, 1);
        context.Api.CmdCopyBufferToImage(commands, depthTransfer.Handle, destination.Image,
            ImageLayout.TransferDstOptimal, 1, &copy);
    }

    private static void Put(Span<float> target, int offset, Vector3 vector, float w)
    {
        target[offset] = vector.X; target[offset + 1] = vector.Y;
        target[offset + 2] = vector.Z; target[offset + 3] = w;
    }

    internal void ReleaseImages()
    {
        foreach (int image in images) if (image != 0) releaseTexture(image);
        if (deviceDepth != 0) releaseTexture(deviceDepth);
        if (depthTransfer != null) frames.DeferDeletion(depthTransfer);
        Array.Clear(images); deviceDepth = width = height = 0; depthTransfer = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (int image in images) if (image != 0) releaseTexture(image);
        if (deviceDepth != 0) releaseTexture(deviceDepth);
        // This owner is retired by FrameRing, or destroyed after device idle.
        depthTransfer?.Dispose();
        deviceDepth = 0; depthTransfer = null;
        Array.Clear(images);
        foreach (Arena arena in arenas) arena.Dispose();
        pipeline.Dispose();
    }
}
