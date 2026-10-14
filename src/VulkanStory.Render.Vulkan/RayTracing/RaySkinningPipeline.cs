using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Shaders;

namespace VulkanStory.Render.Vulkan.RayTracing;

internal sealed unsafe class RaySkinningPipeline : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct Constants
    {
        internal ulong Input, Output, Joints;
        internal uint VertexCount, JointCount, Reset, Padding;
        internal Vector4 PreviousOriginDelta;
    }
    private readonly VulkanContext context;
    private Pipeline pipeline;
    private PipelineLayout layout;
    internal RaySkinningPipeline(VulkanContext context, ShaderCompiler compiler)
    {
        this.context = context;
        try
        {
            using Stream stream = typeof(RaySkinningPipeline).Assembly.GetManifestResourceStream("VulkanStory.RayTracing.skin.comp")!;
            using var reader = new StreamReader(stream);
            var shader = compiler.CompileCompute(reader.ReadToEnd(), "ray-skin.comp");
            if (!shader.Success) throw new InvalidOperationException(shader.Error);
            var range = new PushConstantRange(ShaderStageFlags.ComputeBit, 0, (uint)sizeof(Constants));
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo, PushConstantRangeCount = 1, PPushConstantRanges = &range,
            };
            VulkanResult.Check(context.Api.CreatePipelineLayout(context.Device, &layoutInfo, null, out layout), "ray skinning layout");
            fixed (byte* code = shader.Spirv)
            {
                var moduleInfo = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)shader.Spirv.Length, PCode = (uint*)code,
                };
                ShaderModule module;
                VulkanResult.Check(context.Api.CreateShaderModule(context.Device, &moduleInfo, null, &module), "ray skinning shader");
                try
                {
                    byte* entry = stackalloc byte[5] {(byte)'m',(byte)'a',(byte)'i',(byte)'n',0};
                    var create = new ComputePipelineCreateInfo
                    {
                        SType = StructureType.ComputePipelineCreateInfo, Layout = layout,
                        Stage = new PipelineShaderStageCreateInfo
                        {
                            SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit,
                            Module = module, PName = entry,
                        },
                    };
                    Pipeline created;
                    VulkanResult.Check(context.Api.CreateComputePipelines(context.Device, default, 1, &create, null, &created), "ray skinning pipeline");
                    pipeline = created;
                }
                finally { context.Api.DestroyShaderModule(context.Device, module, null); }
            }
        }
        catch { Dispose(); throw; }
    }
    internal void Record(CommandBuffer commands, VulkanBuffer input, VulkanBuffer output, VulkanBuffer joints,
        uint count, uint jointCount, bool reset, Vector3 previousOriginDelta)
    {
        var constants = new Constants
        {
            Input = RayAccelerationStructure.BufferAddress(context, input), Output = RayAccelerationStructure.BufferAddress(context, output),
            Joints = RayAccelerationStructure.BufferAddress(context, joints), VertexCount = count, JointCount = jointCount,
            Reset = reset ? 1u : 0u, PreviousOriginDelta = new Vector4(previousOriginDelta, 0),
        };
        context.Api.CmdBindPipeline(commands, PipelineBindPoint.Compute, pipeline);
        context.Api.CmdPushConstants(commands, layout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(Constants), &constants);
        context.Api.CmdDispatch(commands, (count + 63) / 64, 1, 1);
    }
    public void Dispose()
    {
        if (pipeline.Handle != 0) context.Api.DestroyPipeline(context.Device, pipeline, null);
        if (layout.Handle != 0) context.Api.DestroyPipelineLayout(context.Device, layout, null);
        pipeline = default; layout = default;
    }
}
