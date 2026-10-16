using Silk.NET.Vulkan;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    private void ResolveGlDepthStencil(VulkanTexture source, uint sourceLevel, uint sourceLayer, VulkanTexture destination,
        uint destinationLevel, uint destinationLayer, TextureBlitRectangle sourceRect, TextureBlitRectangle destinationRect,
        int sourceWidth, int sourceHeight, int destinationWidth, int destinationHeight, Rect2D clip, ImageAspectFlags aspect, int destinationId)
    {
        if (!_frameActive)
        {
            RecordGlGraphics(() => ResolveGlDepthStencil(source,sourceLevel,sourceLayer,destination,destinationLevel,destinationLayer,
                sourceRect,destinationRect,sourceWidth,sourceHeight,destinationWidth,destinationHeight,clip,aspect,destinationId));
            return;
        }
        EndNativePass(); _targets.EndPass(Commands); _targets.FlushAllPendingClears(Commands); _targets.EndRendering(Commands);
        var support=new PhysicalDeviceDepthStencilResolveProperties { SType=StructureType.PhysicalDeviceDepthStencilResolveProperties };
        var properties=new PhysicalDeviceProperties2 { SType=StructureType.PhysicalDeviceProperties2,PNext=&support };
        _context.Api.GetPhysicalDeviceProperties2(_context.PhysicalDevice,&properties);
        ResolveModeFlags modes=aspect==ImageAspectFlags.DepthBit ? support.SupportedDepthResolveModes : support.SupportedStencilResolveModes;
        if((modes&ResolveModeFlags.SampleZeroBit)==0) throw new NotSupportedException("The device has no sample-zero depth/stencil resolve mode.");
        long sourceDX=(long)sourceRect.X1-sourceRect.X0, sourceDY=(long)sourceRect.Y1-sourceRect.Y0;
        long destinationDX=(long)destinationRect.X1-destinationRect.X0, destinationDY=(long)destinationRect.Y1-destinationRect.Y0;
        if(Math.Abs(sourceDX)!=Math.Abs(destinationDX) || Math.Abs(sourceDY)!=Math.Abs(destinationDY))
            throw new ArgumentException("Multisample depth/stencil transfers require equal rectangle dimensions.");
        int signX=sourceDX==destinationDX ? 1 : -1, signY=sourceDY==destinationDY ? 1 : -1;
        long offsetX=signX>0 ? (long)sourceRect.X0-destinationRect.X0 : (long)sourceRect.X0+destinationRect.X0-1;
        long offsetY=signY>0 ? (long)sourceRect.Y0-destinationRect.Y0 : (long)sourceRect.Y0+destinationRect.Y0-1;
        long left=Math.Max(clip.Offset.X,signX>0 ? -offsetX : offsetX-sourceWidth+1);
        long right=Math.Min((long)clip.Offset.X+clip.Extent.Width,signX>0 ? sourceWidth-offsetX : offsetX+1);
        long bottom=Math.Max(clip.Offset.Y,signY>0 ? -offsetY : offsetY-sourceHeight+1);
        long top=Math.Min((long)clip.Offset.Y+clip.Extent.Height,signY>0 ? sourceHeight-offsetY : offsetY+1);
        if(left>=right || bottom>=top) return;
        int multiId=0,singleId=0;
        try
        {
            uint width=(uint)Math.Min(256,right-left), height=(uint)Math.Min(256,top-bottom);
            multiId=_textures.CreateRenderbuffer(width,height,source.Format,source.Aspect,(int)source.Samples,out int samples);
            if((SampleCountFlags)samples!=source.Samples) throw new NotSupportedException("Resolve scratch storage has no matching sample count.");
            singleId=_textures.CreateRenderbuffer(width,height,source.Format,source.Aspect,0,out _);
            VulkanTexture multi=_textures.Get(multiId)!, single=_textures.Get(singleId)!;
            for(long y=bottom;y<top;y+=256) for(long x=left;x<right;x+=256)
            {
                int w=(int)Math.Min(256,right-x), h=(int)Math.Min(256,top-y);
                int firstX=(int)(signX*x+offsetX), firstY=(int)(signY*y+offsetY);
                int sx=signX>0 ? firstX : firstX-w+1, sy=signY>0 ? firstY : firstY-h+1;
                TransferImages(source,sourceLevel,sourceLayer,multi,0,0,sx,sy,0,0,w,h,aspect);
                _textures.Require(_barriers,Commands,multi,ResourceUsage.DepthStencilResolveSource);
                _textures.Require(_barriers,Commands,single,ResourceUsage.DepthStencilResolveDestination);
                _barriers.Flush(Commands);
                var attachment=new RenderingAttachmentInfo { SType=StructureType.RenderingAttachmentInfo,
                    ImageView=multi.ViewOfLayer(0),ImageLayout=ImageLayout.DepthStencilAttachmentOptimal,
                    ResolveMode=ResolveModeFlags.SampleZeroBit,ResolveImageView=single.ViewOfLayer(0),
                    ResolveImageLayout=ImageLayout.DepthStencilAttachmentOptimal,LoadOp=AttachmentLoadOp.Load,StoreOp=AttachmentStoreOp.DontCare };
                var rendering=new RenderingInfo { SType=StructureType.RenderingInfo,LayerCount=1,
                    RenderArea=new Rect2D(new Offset2D(),new Extent2D((uint)w,(uint)h)),
                    PDepthAttachment=aspect==ImageAspectFlags.DepthBit ? &attachment : null,
                    PStencilAttachment=aspect==ImageAspectFlags.StencilBit ? &attachment : null };
                _context.Api.CmdBeginRendering(Commands,&rendering);
                _context.Api.CmdEndRendering(Commands);
                var readRect=new TextureBlitRectangle(signX>0 ? 0 : w,signY>0 ? 0 : h,signX>0 ? w : 0,signY>0 ? h : 0);
                var writeRect=new TextureBlitRectangle((int)x,(int)y,(int)x+w,(int)y+h);
                var tileClip=new Rect2D(new Offset2D((int)x,(int)y),new Extent2D((uint)w,(uint)h));
                if(aspect==ImageAspectFlags.StencilBit)
                    TransformGlStencil(single,0,0,destination,destinationLevel,destinationLayer,readRect,writeRect,w,h,tileClip);
                else
                    DrawGlBlit(singleId,0,destinationId,(int)destinationLevel,(int)destinationLayer,readRect,writeRect,
                        w,h,destinationWidth,destinationHeight,0,0,tileClip,false,true,false);
            }
        }
        finally { if(multiId!=0) DeleteTexture(multiId); if(singleId!=0) DeleteTexture(singleId); }
    }
}
