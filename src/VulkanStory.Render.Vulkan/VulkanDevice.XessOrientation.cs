using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    private int xessUprightInputsProgram, xessRendererOutputProgram;
    /// <summary>Actual upright SDK resources, tagged with their producer frame.</summary>
    internal (ulong FrameId, int Color, int Depth, int Motion, int Output) LastXessInputTextures { get; private set; }

    private bool PrepareXessUprightInputs(in UpscalerFrame frame, int color, int depth, int motion)
    {
        if (xessUprightInputsProgram == 0)
            xessUprightInputsProgram = CreateComputeProgram("""
                #version 450
                layout(local_size_x=8,local_size_y=8) in;
                layout(set=0,binding=0) uniform sampler2D scene;
                layout(set=0,binding=1) uniform sampler2D sceneDepth;
                layout(set=0,binding=2) uniform sampler2D sceneMotion;
                layout(set=0,binding=3,rgba16f) writeonly uniform image2D sdkColor;
                layout(set=0,binding=4,r32f) writeonly uniform image2D sdkDepth;
                layout(set=0,binding=5,rg16f) writeonly uniform image2D sdkMotion;
                void main() {
                    ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);
                    ivec2 size = imageSize(sdkColor);
                    if (any(greaterThanEqual(pixel,size))) return;
                    ivec2 source = ivec2(pixel.x,size.y-1-pixel.y);
                    imageStore(sdkColor,pixel,texelFetch(scene,source,0));
                    imageStore(sdkDepth,pixel,vec4(texelFetch(sceneDepth,source,0).r));
                    vec2 velocity = texelFetch(sceneMotion,source,0).rg;
                    imageStore(sdkMotion,pixel,vec4(velocity.x,-velocity.y,0,0));
                }
                """, "xess-upright-inputs", [new(0,ComputeSlotKind.Sampled),new(1,ComputeSlotKind.Sampled),
                    new(2,ComputeSlotKind.Sampled),new(3,ComputeSlotKind.Storage),new(4,ComputeSlotKind.Storage),new(5,ComputeSlotKind.Storage)]);
        return xessUprightInputsProgram != 0 && RecordComputePass(new ComputePassDeclaration
        {
            Name = "XeSS upright inputs", ProgramId = xessUprightInputsProgram,
            Bindings = [new(0,frame.Color,ComputeAccess.Sampled),new(1,frame.Depth,ComputeAccess.Sampled),
                new(2,frame.Motion,ComputeAccess.Sampled),new(3,color,ComputeAccess.StorageWrite),
                new(4,depth,ComputeAccess.StorageWrite),new(5,motion,ComputeAccess.StorageWrite)],
            Dispatches = [ComputeDispatch.Covering(3)],
        });
    }

    private bool RestoreXessRendererOrientation(int source, int output)
    {
        if (xessRendererOutputProgram == 0)
            xessRendererOutputProgram = CreateComputeProgram("""
                #version 450
                layout(local_size_x=8,local_size_y=8) in;
                layout(set=0,binding=0) uniform sampler2D sdkOutput;
                layout(set=0,binding=1,rgba16f) writeonly uniform image2D rendererOutput;
                void main() {
                    ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);
                    ivec2 size = imageSize(rendererOutput);
                    if (any(greaterThanEqual(pixel,size))) return;
                    imageStore(rendererOutput,pixel,texelFetch(sdkOutput,ivec2(pixel.x,size.y-1-pixel.y),0));
                }
                """, "xess-renderer-output", [new(0,ComputeSlotKind.Sampled),new(1,ComputeSlotKind.Storage)]);
        return xessRendererOutputProgram != 0 && RecordComputePass(new ComputePassDeclaration
        {
            Name = "XeSS renderer output", ProgramId = xessRendererOutputProgram,
            Bindings = [new(0,source,ComputeAccess.Sampled),new(1,output,ComputeAccess.StorageWrite)],
            Dispatches = [ComputeDispatch.Covering(1)],
        });
    }
}
