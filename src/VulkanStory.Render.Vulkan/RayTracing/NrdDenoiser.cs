using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Render.Vulkan.RayTracing;

/// <summary>Records NRD's native Vulkan compute schedule using renderer-owned images and frame-slot descriptors.</summary>
internal sealed unsafe class NrdDenoiser : IDisposable
{
    private sealed class Kernel
    {
        internal Pipeline Pipeline;
        internal PipelineLayout Layout;
        internal DescriptorSetLayout Images;
    }
    private readonly VulkanContext context;
    private readonly FrameRing frames;
    private readonly TextureManager textures;
    private readonly Action<int> release;
    private nint instance;
    private NrdApi.Info info;
    private readonly List<Kernel?> kernels = [];
    private readonly Sampler[] samplers = new Sampler[2];
    private DescriptorSetLayout constants;
    private readonly DescriptorPool[] arenas;
    private readonly ulong[] arenaFrames;
    private int[] permanent = [], transient = [];
    private int width, height;
    private bool disposed;
    internal int Diffuse { get; private set; }
    internal int Specular { get; private set; }

    internal NrdDenoiser(VulkanContext context, FrameRing frames, TextureManager textures, Action<int> release)
    {
        this.context = context; this.frames = frames; this.textures = textures; this.release = release;
        arenas = new DescriptorPool[frames.FramesInFlight];
        arenaFrames = Enumerable.Repeat(ulong.MaxValue, frames.FramesInFlight).ToArray();
        try
        {
            NrdApi.Check(NrdApi.Create(out instance, out info), "NRD instance creation");
            if (info.ConstantSet != 1 || info.ResourceSet != 0)
                throw new NotSupportedException("Unexpected NRD descriptor spaces.");
            CreateConstantsLayout();
            for (uint i = 0; i < info.Pipelines; i++) kernels.Add(null);
            DescriptorPoolSize* sizes = stackalloc DescriptorPoolSize[4]
                {
                    new(DescriptorType.SampledImage, Math.Max(info.Textures, 1) * 2),
                    new(DescriptorType.StorageImage, Math.Max(info.Storage, 1) * 2),
                    new(DescriptorType.UniformBuffer, info.Sets * 2),
                    new(DescriptorType.Sampler, info.Sets * 4),
                };
            for (int i = 0; i < arenas.Length; i++)
            {
                var create = new DescriptorPoolCreateInfo
                {
                    SType = StructureType.DescriptorPoolCreateInfo, MaxSets = info.Sets * 4,
                    PoolSizeCount = 4, PPoolSizes = sizes,
                };
                VulkanResult.Check(context.Api.CreateDescriptorPool(context.Device, &create, null, out arenas[i]), "NRD descriptor arena");
            }
        }
        catch { Dispose(); throw; }
    }

    private void CreateConstantsLayout()
    {
        for (int i = 0; i < 2; i++)
        {
            Filter filter = i == 0 ? Filter.Nearest : Filter.Linear;
            var create = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo, MinFilter = filter, MagFilter = filter,
                MipmapMode = i == 0 ? SamplerMipmapMode.Nearest : SamplerMipmapMode.Linear,
                AddressModeU = SamplerAddressMode.ClampToEdge, AddressModeV = SamplerAddressMode.ClampToEdge,
                AddressModeW = SamplerAddressMode.ClampToEdge, MaxLod = float.MaxValue,
            };
            VulkanResult.Check(context.Api.CreateSampler(context.Device, &create, null, out samplers[i]), "NRD sampler");
        }
        DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[3];
        fixed (Sampler* samplerPointer = samplers)
        {
            for (uint i = 0; i < 2; i++) bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = info.SamplerOffset + i, DescriptorType = DescriptorType.Sampler,
                DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit, PImmutableSamplers = samplerPointer + i,
            };
            bindings[2] = new DescriptorSetLayoutBinding
            {
                Binding = info.ConstantOffset, DescriptorType = DescriptorType.UniformBuffer,
                DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit,
            };
            var create = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 3, PBindings = bindings,
            };
            VulkanResult.Check(context.Api.CreateDescriptorSetLayout(context.Device, &create, null, out constants), "NRD constants layout");
        }
    }

    private Kernel CreateKernel(NrdApi.Pipeline metadata, int index)
    {
        var kernel = new Kernel(); kernels[index] = kernel;
        var bindings = new DescriptorSetLayoutBinding[metadata.Textures + metadata.Storage];
        for (uint i = 0; i < bindings.Length; i++) bindings[i] = new DescriptorSetLayoutBinding
        {
            Binding = i < metadata.Textures ? info.TextureOffset + i : info.StorageOffset + i - metadata.Textures,
            DescriptorType = i < metadata.Textures ? DescriptorType.SampledImage : DescriptorType.StorageImage,
            DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit,
        };
        fixed (DescriptorSetLayoutBinding* pointer = bindings)
        {
            var create = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = (uint)bindings.Length, PBindings = pointer,
            };
            VulkanResult.Check(context.Api.CreateDescriptorSetLayout(context.Device, &create, null, out kernel.Images), "NRD image layout");
        }
        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[2] {kernel.Images, constants};
        var layoutCreate = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 2, PSetLayouts = layouts,
        };
        VulkanResult.Check(context.Api.CreatePipelineLayout(context.Device, &layoutCreate, null, out kernel.Layout), "NRD pipeline layout");
        var moduleCreate = new ShaderModuleCreateInfo
        {
            SType = StructureType.ShaderModuleCreateInfo, CodeSize = checked((nuint)metadata.Bytes), PCode = (uint*)metadata.Shader,
        };
        ShaderModule module;
        VulkanResult.Check(context.Api.CreateShaderModule(context.Device, &moduleCreate, null, &module), "NRD shader module");
        try
        {
            byte* entry = stackalloc byte[5] {(byte)'m', (byte)'a', (byte)'i', (byte)'n', 0};
            var create = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo, Layout = kernel.Layout,
                Stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit, Module = module, PName = entry,
                },
            };
            Pipeline pipeline;
            VulkanResult.Check(context.Api.CreateComputePipelines(context.Device, default, 1, &create, null, &pipeline), "NRD compute pipeline");
            kernel.Pipeline = pipeline;
        }
        finally { context.Api.DestroyShaderModule(context.Device, module, null); }
        return kernel;
    }

    private void EnsureImages(int nextWidth, int nextHeight)
    {
        if (width == nextWidth && height == nextHeight) return;
        ReleaseImages();
        try
        {
            permanent = CreatePool(info.Permanent, true, nextWidth, nextHeight);
            transient = CreatePool(info.Transient, false, nextWidth, nextHeight);
            Diffuse = textures.CreateStorage((uint)nextWidth, (uint)nextHeight, Format.R16G16B16A16Sfloat);
            Specular = textures.CreateStorage((uint)nextWidth, (uint)nextHeight, Format.R16G16B16A16Sfloat);
            width = nextWidth; height = nextHeight;
        }
        catch { ReleaseImages(); throw; }
    }

    private int[] CreatePool(uint count, bool persistent, int w, int h)
    {
        var pool = new int[count];
        try
        {
            for (uint i = 0; i < count; i++)
            {
                NrdApi.Check(NrdApi.GetTexture(instance, persistent ? 1u : 0u, i, out uint format, out uint factor), "NRD texture metadata");
                if (factor == 0) throw new InvalidOperationException("NRD texture has zero scale.");
                Format target = FormatOf(format);
                pool[i] = textures.CreateStorage(((uint)w + factor - 1) / factor, ((uint)h + factor - 1) / factor, target);
                if (textures.Get(pool[i])!.Format != target) throw new NotSupportedException("NRD storage format unavailable: " + target);
            }
            return pool;
        }
        catch { foreach (int image in pool) if (image != 0) release(image); throw; }
    }

    // NRD's public format enum is pinned to 4.17.3; the bridge reports that enum without reinterpretation.
    private static Format FormatOf(uint format)
    {
        ReadOnlySpan<Format> formats = [
            Format.R8Unorm, Format.R8SNorm, Format.R8Uint, Format.R8Sint,
            Format.R8G8Unorm, Format.R8G8SNorm, Format.R8G8Uint, Format.R8G8Sint,
            Format.R8G8B8A8Unorm, Format.R8G8B8A8SNorm, Format.R8G8B8A8Uint, Format.R8G8B8A8Sint, Format.R8G8B8A8Srgb,
            Format.R16Unorm, Format.R16SNorm, Format.R16Uint, Format.R16Sint, Format.R16Sfloat,
            Format.R16G16Unorm, Format.R16G16SNorm, Format.R16G16Uint, Format.R16G16Sint, Format.R16G16Sfloat,
            Format.R16G16B16A16Unorm, Format.R16G16B16A16SNorm, Format.R16G16B16A16Uint, Format.R16G16B16A16Sint, Format.R16G16B16A16Sfloat,
            Format.R32Uint, Format.R32Sint, Format.R32Sfloat, Format.R32G32Uint, Format.R32G32Sint, Format.R32G32Sfloat,
            Format.R32G32B32Uint, Format.R32G32B32Sint, Format.R32G32B32Sfloat,
            Format.R32G32B32A32Uint, Format.R32G32B32A32Sint, Format.R32G32B32A32Sfloat,
            Format.A2B10G10R10UnormPack32, Format.A2B10G10R10UintPack32, Format.B10G11R11UfloatPack32, Format.E5B9G9R9UfloatPack32,
        ];
        return format < formats.Length ? formats[(int)format] : throw new NotSupportedException("Unknown NRD format: " + format);
    }

    internal void Denoise(CommandBuffer commands, RayTracingRenderer source, in RayTracingFrame frame,
        ulong frameIdentity, BarrierBatcher barriers)
    {
        bool reset = width != source.Width || height != source.Height;
        EnsureImages(source.Width, source.Height);
        int slot = frames.Current.Index;
        if (arenaFrames[slot] != frameIdentity)
        {
            VulkanResult.Check(context.Api.ResetDescriptorPool(context.Device, arenas[slot], 0), "NRD completed descriptor slot");
            arenaFrames[slot] = frameIdentity;
        }
        uint count;
        fixed (float* projection = frame.Projection.Span)
        fixed (float* previousProjection = frame.PreviousProjection.Span)
        fixed (float* view = frame.View.Span)
        fixed (float* previousView = frame.PreviousView.Span)
            NrdApi.Check(NrdApi.Begin(instance, projection, previousProjection, view, previousView,
                (uint)width, (uint)height, frame.OriginDelta.X, frame.OriginDelta.Y, frame.OriginDelta.Z,
                frame.Jitter.X, frame.Jitter.Y, frame.Milliseconds, frame.FarPlane,
                reset || frame.Reset ? 1u : 0u, out count), "NRD frame settings");
        for (uint i = 0; i < count; i++) RecordDispatch(commands, source, i, arenas[slot], barriers);
    }

    private void RecordDispatch(CommandBuffer commands, RayTracingRenderer source, uint index,
        DescriptorPool pool, BarrierBatcher barriers)
    {
        NrdApi.Check(NrdApi.GetDispatch(instance, index, out var dispatch), "NRD dispatch");
        int pipelineIndex = checked((int)dispatch.Pipeline);
        var kernel = kernels[pipelineIndex];
        if (kernel == null)
        {
            NrdApi.Check(NrdApi.GetPipeline(instance, dispatch.Pipeline, out var metadata), "NRD pipeline metadata");
            kernel = CreateKernel(metadata, pipelineIndex);
        }
        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[2] {kernel.Images, constants};
        DescriptorSet* sets = stackalloc DescriptorSet[2];
        var allocate = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = pool, DescriptorSetCount = 2, PSetLayouts = layouts,
        };
        VulkanResult.Check(context.Api.AllocateDescriptorSets(context.Device, &allocate, sets), "NRD dispatch descriptors");
        int bytes = Math.Max(16, checked((int)dispatch.Bytes));
        if (!frames.Current.TryAllocateUniforms(bytes, out RingAllocation uniform))
            throw new InvalidOperationException("NRD frame constants exceed the uniform ring.");
        var data = new Span<byte>((void*)uniform.Pointer, bytes);
        data.Clear();
        if (dispatch.Bytes != 0) new ReadOnlySpan<byte>((void*)dispatch.Constants, (int)dispatch.Bytes).CopyTo(data);
        var constantBuffer = new DescriptorBufferInfo(uniform.Buffer, uniform.Offset, (ulong)bytes);
        var writeConstants = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstSet = sets[1], DstBinding = info.ConstantOffset,
            DescriptorCount = 1, DescriptorType = DescriptorType.UniformBuffer, PBufferInfo = &constantBuffer,
        };
        context.Api.UpdateDescriptorSets(context.Device, 1, &writeConstants, 0, null);
        DescriptorImageInfo* images = stackalloc DescriptorImageInfo[checked((int)dispatch.Resources)];
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[checked((int)dispatch.Resources)];
        uint readBinding = info.TextureOffset, writeBinding = info.StorageOffset;
        for (uint i = 0; i < dispatch.Resources; i++)
        {
            NrdApi.Check(NrdApi.GetResource(instance, index, i, out uint kind, out uint poolIndex, out uint storage), "NRD resource mapping");
            int id = kind switch
            {
                0 => source.Texture(RayTracingRenderer.Motion), 1 => source.Texture(RayTracingRenderer.NormalRoughness),
                2 => source.Texture(RayTracingRenderer.ViewDepth), 3 => source.Texture(RayTracingRenderer.DiffuseSignal),
                4 => source.Texture(RayTracingRenderer.SpecularSignal), 5 => Diffuse, 6 => Specular,
                7 => transient[checked((int)poolIndex)], 8 => permanent[checked((int)poolIndex)],
                _ => throw new NotSupportedException("Unsupported NRD resource: " + kind),
            };
            VulkanTexture texture = textures.Get(id)!;
            textures.Require(barriers, commands, texture, storage == 0 ? ResourceUsage.SampleCompute : ResourceUsage.StorageWrite);
            images[i] = new DescriptorImageInfo(default, texture.View, storage == 0 ? ImageLayout.ShaderReadOnlyOptimal : ImageLayout.General);
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = sets[0], DstBinding = storage == 0 ? readBinding++ : writeBinding++,
                DescriptorCount = 1, DescriptorType = storage == 0 ? DescriptorType.SampledImage : DescriptorType.StorageImage,
                PImageInfo = &images[i],
            };
        }
        barriers.Flush(commands);
        context.Api.UpdateDescriptorSets(context.Device, dispatch.Resources, writes, 0, null);
        context.Api.CmdBindPipeline(commands, PipelineBindPoint.Compute, kernel.Pipeline);
        context.Api.CmdBindDescriptorSets(commands, PipelineBindPoint.Compute, kernel.Layout, 0, 2, sets, 0, null);
        context.Api.CmdDispatch(commands, dispatch.Width, dispatch.Height, 1);
    }

    internal void ReleaseImages()
    {
        foreach (int id in permanent) if (id != 0) release(id);
        foreach (int id in transient) if (id != 0) release(id);
        if (Diffuse != 0) release(Diffuse);
        if (Specular != 0) release(Specular);
        permanent = transient = []; Diffuse = Specular = width = height = 0;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ReleaseImages();
        foreach (var kernel in kernels)
        {
            if (kernel == null) continue;
            if (kernel.Pipeline.Handle != 0) context.Api.DestroyPipeline(context.Device, kernel.Pipeline, null);
            if (kernel.Layout.Handle != 0) context.Api.DestroyPipelineLayout(context.Device, kernel.Layout, null);
            if (kernel.Images.Handle != 0) context.Api.DestroyDescriptorSetLayout(context.Device, kernel.Images, null);
        }
        foreach (var pool in arenas) if (pool.Handle != 0) context.Api.DestroyDescriptorPool(context.Device, pool, null);
        if (constants.Handle != 0) context.Api.DestroyDescriptorSetLayout(context.Device, constants, null);
        foreach (var sampler in samplers) if (sampler.Handle != 0) context.Api.DestroySampler(context.Device, sampler, null);
        if (instance != 0) NrdApi.Destroy(instance);
        instance = 0;
    }
}
