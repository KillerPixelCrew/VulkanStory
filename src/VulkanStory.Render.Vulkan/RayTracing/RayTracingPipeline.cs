using System.Text;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Shaders;

namespace VulkanStory.Render.Vulkan.RayTracing;

/// <summary>The portable ray pipeline and its driver-aligned shader binding table.</summary>
internal sealed unsafe class RayTracingPipeline : IDisposable
{
    internal const ShaderStageFlags Stages = ShaderStageFlags.RaygenBitKhr | ShaderStageFlags.MissBitKhr |
        ShaderStageFlags.ClosestHitBitKhr | ShaderStageFlags.AnyHitBitKhr;
    private readonly VulkanContext context;
    private readonly KhrRayTracingPipeline api;
    private readonly List<ShaderModule> modules = new();
    private VulkanBuffer? table;
    private bool disposed;
    private StridedDeviceAddressRegionKHR generation, miss, hit;
    private uint maximumDispatch;
    internal Pipeline Handle { get; private set; }
    internal PipelineLayout Layout { get; private set; }
    internal DescriptorSetLayout FrameLayout { get; private set; }

    internal RayTracingPipeline(VulkanContext context, ShaderCompiler compiler, DescriptorSetLayout textureLayout)
    {
        this.context = context;
        if (!context.Api.TryGetDeviceExtension(context.Instance, context.Device, out KhrRayTracingPipeline loaded))
            throw new NotSupportedException("The enabled KHR ray-pipeline entry points are unavailable.");
        api = loaded;
        try
        {
            CreateLayout(textureLayout);
            string common = Source("common.glsl");
            (string File, ShaderStageKind Kind, ShaderStageFlags Stage)[] sources =
            [
                ("path.rgen", ShaderStageKind.RayGenerationShader, ShaderStageFlags.RaygenBitKhr),
                ("environment.rmiss", ShaderStageKind.MissShader, ShaderStageFlags.MissBitKhr),
                ("visibility.rmiss", ShaderStageKind.MissShader, ShaderStageFlags.MissBitKhr),
                ("surface.rchit", ShaderStageKind.ClosestHitShader, ShaderStageFlags.ClosestHitBitKhr),
                ("surface.rahit", ShaderStageKind.AnyHitShader, ShaderStageFlags.AnyHitBitKhr),
                ("visibility.rahit", ShaderStageKind.AnyHitShader, ShaderStageFlags.AnyHitBitKhr),
            ];
            var stages = new PipelineShaderStageCreateInfo[sources.Length];
            byte[] main = Encoding.UTF8.GetBytes("main\0");
            fixed (byte* entry = main)
            {
                for (int i = 0; i < sources.Length; i++)
                {
                    var source = sources[i];
                    ShaderCompileResult compiled = compiler.CompileRayTracing(common + "\n" + Source(source.File), source.File, source.Kind);
                    if (!compiled.Success) throw new InvalidOperationException("Path tracing shader " + source.File + ": " + compiled.Error);
                    fixed (byte* code = compiled.Spirv)
                    {
                        var moduleInfo = new ShaderModuleCreateInfo
                        {
                            SType = StructureType.ShaderModuleCreateInfo,
                            CodeSize = (nuint)compiled.Spirv.Length, PCode = (uint*)code,
                        };
                        ShaderModule module;
                        VulkanResult.Check(context.Api.CreateShaderModule(context.Device, &moduleInfo, null, &module),
                            "creating a path tracing shader module");
                        modules.Add(module);
                        stages[i] = new PipelineShaderStageCreateInfo
                        {
                            SType = StructureType.PipelineShaderStageCreateInfo,
                            Stage = source.Stage, Module = module, PName = entry,
                        };
                    }
                }
                RayTracingShaderGroupCreateInfoKHR[] groups =
                [
                    General(0), General(1), General(2),
                    new()
                    {
                        SType = StructureType.RayTracingShaderGroupCreateInfoKhr,
                        Type = RayTracingShaderGroupTypeKHR.TrianglesHitGroupKhr,
                        GeneralShader = Vk.ShaderUnusedKhr, ClosestHitShader = 3, AnyHitShader = 4,
                        IntersectionShader = Vk.ShaderUnusedKhr,
                    },
                    new()
                    {
                        SType = StructureType.RayTracingShaderGroupCreateInfoKhr,
                        Type = RayTracingShaderGroupTypeKHR.TrianglesHitGroupKhr,
                        GeneralShader = Vk.ShaderUnusedKhr, ClosestHitShader = Vk.ShaderUnusedKhr,
                        AnyHitShader = 5, IntersectionShader = Vk.ShaderUnusedKhr,
                    },
                ];
                fixed (PipelineShaderStageCreateInfo* stagePointer = stages)
                fixed (RayTracingShaderGroupCreateInfoKHR* groupPointer = groups)
                {
                    var create = new RayTracingPipelineCreateInfoKHR
                    {
                        SType = StructureType.RayTracingPipelineCreateInfoKhr,
                        StageCount = (uint)stages.Length, PStages = stagePointer,
                        GroupCount = (uint)groups.Length, PGroups = groupPointer,
                        MaxPipelineRayRecursionDepth = 1, Layout = Layout,
                    };
                    Pipeline pipeline;
                    VulkanResult.Check(api.CreateRayTracingPipelines(context.Device, default, default, 1, &create, null, &pipeline),
                        "vkCreateRayTracingPipelinesKHR");
                    Handle = pipeline;
                }
            }
            CreateTable();
        }
        catch { Dispose(); throw; }
    }

    private void CreateLayout(DescriptorSetLayout textureLayout)
    {
        var bindings = new DescriptorSetLayoutBinding[15];
        for (uint binding = 0; binding < bindings.Length; binding++)
        {
            bindings[binding] = new DescriptorSetLayoutBinding
            {
                Binding = binding, DescriptorCount = 1, StageFlags = Stages,
                DescriptorType = binding switch
                {
                    0 => DescriptorType.AccelerationStructureKhr,
                    1 => DescriptorType.UniformBuffer,
                    2 => DescriptorType.StorageBuffer,
                    _ => DescriptorType.StorageImage,
                },
            };
        }
        fixed (DescriptorSetLayoutBinding* pointer = bindings)
        {
            var create = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)bindings.Length, PBindings = pointer,
            };
            DescriptorSetLayout layout;
            VulkanResult.Check(context.Api.CreateDescriptorSetLayout(context.Device, &create, null, &layout), "path tracing frame layout");
            FrameLayout = layout;
        }
        DescriptorSetLayout* sets = stackalloc DescriptorSetLayout[2] { FrameLayout, textureLayout };
        var info = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 2, PSetLayouts = sets,
        };
        PipelineLayout pipelineLayout;
        VulkanResult.Check(context.Api.CreatePipelineLayout(context.Device, &info, null, &pipelineLayout), "path tracing pipeline layout");
        Layout = pipelineLayout;
    }

    private static RayTracingShaderGroupCreateInfoKHR General(uint stage) => new()
    {
        SType = StructureType.RayTracingShaderGroupCreateInfoKhr,
        Type = RayTracingShaderGroupTypeKHR.GeneralKhr, GeneralShader = stage,
        ClosestHitShader = Vk.ShaderUnusedKhr, AnyHitShader = Vk.ShaderUnusedKhr, IntersectionShader = Vk.ShaderUnusedKhr,
    };

    private void CreateTable()
    {
        var properties = new PhysicalDeviceRayTracingPipelinePropertiesKHR
        {
            SType = StructureType.PhysicalDeviceRayTracingPipelinePropertiesKhr,
        };
        var query = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &properties };
        context.Api.GetPhysicalDeviceProperties2(context.PhysicalDevice, &query);
        maximumDispatch = properties.MaxRayDispatchInvocationCount;
        if (properties.ShaderGroupHandleSize == 0 || properties.ShaderGroupHandleAlignment == 0 || properties.ShaderGroupBaseAlignment == 0)
            throw new NotSupportedException("The ray pipeline has invalid shader binding table properties.");
        ulong stride = Align(properties.ShaderGroupHandleSize, properties.ShaderGroupHandleAlignment);
        ulong rayStride = Align(stride, properties.ShaderGroupBaseAlignment);
        if (rayStride > properties.MaxShaderGroupStride || stride > properties.MaxShaderGroupStride)
            throw new NotSupportedException("The shader binding record exceeds the device stride limit.");
        ulong missOffset = Align(rayStride, properties.ShaderGroupBaseAlignment);
        ulong hitOffset = Align(checked(missOffset + stride * 2), properties.ShaderGroupBaseAlignment);
        ulong bytes = checked(hitOffset + stride * 2);
        table = new VulkanBuffer(context, checked(bytes + properties.ShaderGroupBaseAlignment - 1),
            BufferUsageFlags.ShaderBindingTableBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPoolClass.DeviceBuffers);
        ulong address = RayAccelerationStructure.BufferAddress(context, table);
        ulong aligned = Align(address, properties.ShaderGroupBaseAlignment);
        int offset = checked((int)(aligned - address));
        new Span<byte>((void*)table.Mapped, checked((int)table.Size)).Clear();
        byte[] handles = new byte[checked((int)properties.ShaderGroupHandleSize * 5)];
        fixed (byte* handlePointer = handles)
            VulkanResult.Check(api.GetRayTracingShaderGroupHandles(context.Device, Handle, 0, 5, (nuint)handles.Length, handlePointer),
                "vkGetRayTracingShaderGroupHandlesKHR");
        ulong[] offsets = [0, missOffset, missOffset + stride, hitOffset, hitOffset + stride];
        for (int group = 0; group < offsets.Length; group++)
            handles.AsSpan(checked(group * (int)properties.ShaderGroupHandleSize), (int)properties.ShaderGroupHandleSize)
                .CopyTo(new Span<byte>((byte*)table.Mapped + offset + checked((int)offsets[group]), (int)properties.ShaderGroupHandleSize));
        generation = new StridedDeviceAddressRegionKHR { DeviceAddress = aligned, Stride = rayStride, Size = rayStride };
        miss = new StridedDeviceAddressRegionKHR { DeviceAddress = aligned + missOffset, Stride = stride, Size = stride * 2 };
        hit = new StridedDeviceAddressRegionKHR { DeviceAddress = aligned + hitOffset, Stride = stride, Size = stride * 2 };
    }

    internal void Trace(CommandBuffer commands, DescriptorSet frame, DescriptorSet textures, uint width, uint height)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (width == 0 || height == 0 || (ulong)width * height > maximumDispatch) throw new ArgumentOutOfRangeException(nameof(width));
        RayAccelerationStructure.Barrier(context, commands, PipelineStageFlags2.HostBit, AccessFlags2.HostWriteBit,
            PipelineStageFlags2.RayTracingShaderBitKhr, AccessFlags2.ShaderReadBit);
        context.Api.CmdBindPipeline(commands, PipelineBindPoint.RayTracingKhr, Handle);
        DescriptorSet* sets = stackalloc DescriptorSet[2] { frame, textures };
        context.Api.CmdBindDescriptorSets(commands, PipelineBindPoint.RayTracingKhr, Layout, 0, 2, sets, 0, null);
        StridedDeviceAddressRegionKHR rayRegion = generation, missRegion = miss, hitRegion = hit, callableRegion = default;
        api.CmdTraceRays(commands, &rayRegion, &missRegion, &hitRegion, &callableRegion, width, height, 1);
    }

    private static ulong Align(ulong value, ulong alignment) => checked((value + alignment - 1) / alignment * alignment);
    private static string Source(string file)
    {
        using Stream stream = typeof(RayTracingPipeline).Assembly.GetManifestResourceStream("VulkanStory.RayTracing." + file)
            ?? throw new FileNotFoundException("Path tracing shader is missing from the renderer assembly.", file);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        table?.Dispose(); table = null;
        if (Handle.Handle != 0) context.Api.DestroyPipeline(context.Device, Handle, null);
        if (Layout.Handle != 0) context.Api.DestroyPipelineLayout(context.Device, Layout, null);
        if (FrameLayout.Handle != 0) context.Api.DestroyDescriptorSetLayout(context.Device, FrameLayout, null);
        foreach (ShaderModule module in modules) context.Api.DestroyShaderModule(context.Device, module, null);
        modules.Clear();
        api.Dispose();
    }
}
