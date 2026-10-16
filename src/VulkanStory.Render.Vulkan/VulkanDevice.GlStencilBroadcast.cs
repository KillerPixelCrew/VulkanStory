using Silk.NET.Vulkan;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    private int glStencilBroadcastProgram;
    private int StencilBroadcastProgram()
    {
        if(glStencilBroadcastProgram!=0 && IsProgram(glStencilBroadcastProgram)) return glStencilBroadcastProgram;
        var vertex=new ShaderStageDefinition { Type=ShaderStageType.VertexShader,Code="""
            #version 330 core
            void main(){ vec2 p=vec2(float((gl_VertexID&1)<<2),float((gl_VertexID&2)<<1))-1.; gl_Position=vec4(p,0,1); }
            """ };
        var fragment=new ShaderStageDefinition { Type=ShaderStageType.FragmentShader,Code="""
            #version 330 core
            uniform usampler2D stencilSource;
            uniform vec2 destination0;
            uniform vec2 source0;
            uniform vec2 sourceStep;
            uniform int bitMask;
            void main(){
                ivec2 d=ivec2(floor(gl_FragCoord.xy))-ivec2(destination0);
                uint value=texelFetch(stencilSource,ivec2(source0)+d*ivec2(sourceStep),0).r;
                if(bitMask!=0 && (value&uint(bitMask))==0u) discard;
                // Keep stencil updates after the fragment's discard decision.
                gl_FragDepth=0.;
            }
            """ };
        if(!CompileShader(vertex)||!CompileShader(fragment)) throw new InvalidOperationException("Stencil broadcast shader staging failed.");
        glStencilBroadcastProgram=LinkProgram(new ShaderProgramDefinition("gl-stencil-broadcast",vertex,fragment,null,null));
        if(glStencilBroadcastProgram==0) throw new InvalidOperationException("Stencil broadcast shader failed: "+GetError());
        return glStencilBroadcastProgram;
    }

    private void BroadcastGlStencil(VulkanTexture source,uint sourceLevel,uint sourceLayer,int destinationId,uint destinationLevel,uint destinationLayer,
        TextureBlitRectangle sourceRect,TextureBlitRectangle destinationRect,int sourceWidth,int sourceHeight,int destinationWidth,int destinationHeight,Rect2D clip)
    {
        if(!_frameActive)
        {
            RecordGlGraphics(()=>BroadcastGlStencil(source,sourceLevel,sourceLayer,destinationId,destinationLevel,destinationLayer,
                sourceRect,destinationRect,sourceWidth,sourceHeight,destinationWidth,destinationHeight,clip)); return;
        }
        UnitBlit? planned=PlanUnitBlit(sourceRect,destinationRect,sourceWidth,sourceHeight,clip);
        if(planned is not { } map) return;
        EndNativePass(); _targets.EndPass(Commands); _targets.FlushAllPendingClears(Commands); _targets.EndRendering(Commands);
        int sampleId=0; VulkanBuffer? bytes=null;
        try
        {
            uint width=(uint)Math.Min(256,map.Right-map.Left),height=(uint)Math.Min(256,map.Top-map.Bottom);
            sampleId=_textures.CreateTransferSampled(width,height,Format.R8Uint);
            VulkanTexture sample=_textures.Get(sampleId)!;
            ulong capacity=((ulong)width*height+3)&~3UL;
            bytes=new VulkanBuffer(_context,capacity,BufferUsageFlags.TransferDstBit|BufferUsageFlags.TransferSrcBit,
                MemoryPropertyFlags.DeviceLocalBit,MemoryPoolClass.DeviceBuffers);
            bool reused=false;
            for(long y=map.Bottom;y<map.Top;y+=256) for(long x=map.Left;x<map.Right;x+=256)
            {
                EndNativePass(); _targets.EndPass(Commands); _targets.EndRendering(Commands);
                int w=(int)Math.Min(256,map.Right-x),h=(int)Math.Min(256,map.Top-y);
                int firstX=(int)(map.SignX*x+map.OffsetX),firstY=(int)(map.SignY*y+map.OffsetY);
                int sx=map.SignX>0?firstX:firstX-w+1,sy=map.SignY>0?firstY:firstY-h+1;
                if(reused) StencilBufferBarrier(Commands,bytes,PipelineStageFlags2.CopyBit,AccessFlags2.TransferReadBit|AccessFlags2.TransferWriteBit,
                    PipelineStageFlags2.CopyBit,AccessFlags2.TransferWriteBit);
                _textures.Require(_barriers,Commands,source,ResourceUsage.TransferSrc); _barriers.Flush(Commands);
                var read=new BufferImageCopy { ImageSubresource=new ImageSubresourceLayers(ImageAspectFlags.StencilBit,sourceLevel,sourceLayer,1),
                    ImageOffset=new Offset3D(sx,sy,0),ImageExtent=new Extent3D((uint)w,(uint)h,1) };
                _context.Api.CmdCopyImageToBuffer(Commands,source.Image,ImageLayout.TransferSrcOptimal,bytes.Handle,1,&read);
                StencilBufferBarrier(Commands,bytes,PipelineStageFlags2.CopyBit,AccessFlags2.TransferWriteBit,PipelineStageFlags2.CopyBit,AccessFlags2.TransferReadBit);
                _textures.Require(_barriers,Commands,sample,ResourceUsage.TransferDst); _barriers.Flush(Commands);
                // Both transfers use tightly packed bytes. No format conversion or
                // optional shader stencil-export capability is required.
                var write=new BufferImageCopy { ImageSubresource=new ImageSubresourceLayers(ImageAspectFlags.ColorBit,0,0,1),ImageExtent=new Extent3D((uint)w,(uint)h,1) };
                _context.Api.CmdCopyBufferToImage(Commands,bytes.Handle,sample.Image,ImageLayout.TransferDstOptimal,1,&write);
                DrawGlStencilBits(destinationId,(int)destinationLevel,(int)destinationLayer,sampleId,destinationWidth,destinationHeight,w,h,map.SignX,map.SignY,
                    new Rect2D(new Offset2D((int)x,(int)y),new Extent2D((uint)w,(uint)h)));
                reused=true;
            }
        }
        finally { if(sampleId!=0) DeleteTexture(sampleId); if(bytes is not null) _frames.DeferDeletion(bytes); }
    }

    private void DrawGlStencilBits(int destination,int level,int layer,int source,int width,int height,int w,int h,int signX,int signY,Rect2D clip)
    {
        EndNativePass(); _targets.EndPass(Commands); _targets.EndRendering(Commands);
        Action<CommandBuffer>? resume=_targets.ScopeOpened; _targets.ScopeOpened=null;
        int clipMask=DisableGlClipping(), target=0;
        try
        {
            target=CreateFramebuffer(width,height); AttachFramebufferTextureLevel(target,0x8D20,destination,level,layer);
            int program=StencilBroadcastProgram();
            SetUniform(program,GetUniformLocation(program,"destination0"),(float)clip.Offset.X,(float)clip.Offset.Y);
            SetUniform(program,GetUniformLocation(program,"source0"),signX>0?0f:w-1f,signY>0?0f:h-1f);
            SetUniform(program,GetUniformLocation(program,"sourceStep"),(float)signX,(float)signY);
            // Zero only the clipped rectangle, then replace one stencil bit in
            // every covered sample. Attaching stencil alone preserves depth.
            for(int bit=-1;bit<8;bit++)
            {
                uint mask=bit<0?255u:1u<<bit;
                StencilOpState state=NativePipelineDescription.DefaultStencil;
                state.WriteMask=mask; state.Reference=mask; state.PassOp=bit<0?StencilOp.Zero:StencilOp.Replace;
                NativePipeline pipeline=RequestNativePipeline(new NativePipelineDescription { ProgramId=program,
                    Targets=NativeTargetFormats(target,0)!,StencilTest=true,StencilFront=state,StencilBack=state },out string reason)
                    ?? throw new InvalidOperationException(reason);
                SetUniform(program,GetUniformLocation(program,"bitMask"),bit<0?0:(int)mask);
                if(!BeginNativePass(new NativePassDescription { Name="GL/StencilBroadcast",FramebufferId=target,ColorSlots=0,Reads=[source],
                    ViewportWidth=width,ViewportHeight=height,Scissor=clip }) ||
                    !DrawNativeFullscreen(pipeline,[new NativeTexture(pipeline.Sampler("stencilSource"),source)],requirePipeline:true))
                    throw new InvalidOperationException("Stencil broadcast failed: "+NativeDrawRefusal);
            }
        }
        finally { try { EndNativePass(); if(target!=0) DeleteFramebuffer(target); } finally { SetGlClipMask(clipMask); _targets.ScopeOpened=resume; } }
    }
}
