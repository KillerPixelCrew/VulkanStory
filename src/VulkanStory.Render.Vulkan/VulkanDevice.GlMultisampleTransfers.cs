using Silk.NET.Vulkan;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    private readonly record struct UnitBlit(int SignX,int SignY,long OffsetX,long OffsetY,long Left,long Bottom,long Right,long Top);

    private static UnitBlit? PlanUnitBlit(TextureBlitRectangle source,TextureBlitRectangle destination,int width,int height,Rect2D clip)
    {
        long sx=(long)source.X1-source.X0, sy=(long)source.Y1-source.Y0;
        long dx=(long)destination.X1-destination.X0, dy=(long)destination.Y1-destination.Y0;
        if(Math.Abs(sx)!=Math.Abs(dx) || Math.Abs(sy)!=Math.Abs(dy)) throw new ArgumentException("Multisample transfers require equal rectangle dimensions.");
        int signX=sx==dx?1:-1, signY=sy==dy?1:-1;
        long ox=signX>0?(long)source.X0-destination.X0:(long)source.X0+destination.X0-1;
        long oy=signY>0?(long)source.Y0-destination.Y0:(long)source.Y0+destination.Y0-1;
        long left=Math.Max(clip.Offset.X,signX>0?-ox:ox-width+1), right=Math.Min((long)clip.Offset.X+clip.Extent.Width,signX>0?width-ox:ox+1);
        long bottom=Math.Max(clip.Offset.Y,signY>0?-oy:oy-height+1), top=Math.Min((long)clip.Offset.Y+clip.Extent.Height,signY>0?height-oy:oy+1);
        return left<right && bottom<top ? new UnitBlit(signX,signY,ox,oy,left,bottom,right,top) : null;
    }

    private void TransformGlMultisample(VulkanTexture source,uint sourceLevel,uint sourceLayer,VulkanTexture destination,
        uint destinationLevel,uint destinationLayer,TextureBlitRectangle sourceRect,TextureBlitRectangle destinationRect,
        int sourceWidth,int sourceHeight,Rect2D clip,ImageAspectFlags aspect)
    {
        UnitBlit? planned=PlanUnitBlit(sourceRect,destinationRect,sourceWidth,sourceHeight,clip);
        if(planned is not { } map) return;
        EndNativePass();
        int scratchId=0;
        try
        {
            scratchId=_textures.CreateRenderbuffer((uint)Math.Min(256,map.Right-map.Left),(uint)Math.Min(256,map.Top-map.Bottom),
                source.Format,source.Aspect,(int)source.Samples,out int samples);
            if((SampleCountFlags)samples!=source.Samples) throw new NotSupportedException("Multisample copy scratch has no matching sample count.");
            VulkanTexture scratch=_textures.Get(scratchId)!;
            CommandBuffer command=_frameActive?Commands:_uploads.BeginRecording(inlineInFrame:false);
            try
            {
                if(_frameActive) { _targets.FlushAllPendingClears(command); _targets.EndRendering(command); }
                // VkImageCopy preserves sample indices but cannot reverse an axis.
                // Gather columns, then scatter rows through a bounded MSAA tile.
                ImageCopy* regions=stackalloc ImageCopy[256];
                for(long y=map.Bottom;y<map.Top;y+=256) for(long x=map.Left;x<map.Right;x+=256)
                {
                    int w=(int)Math.Min(256,map.Right-x), h=(int)Math.Min(256,map.Top-y);
                    int firstX=(int)(map.SignX*x+map.OffsetX), firstY=(int)(map.SignY*y+map.OffsetY);
                    int minY=map.SignY>0?firstY:firstY-h+1;
                    _textures.Require(_barriers,command,source,ResourceUsage.TransferSrc);
                    _textures.Require(_barriers,command,scratch,ResourceUsage.TransferDst); _barriers.Flush(command);
                    uint count=map.SignX>0?1u:(uint)w;
                    for(int column=0;column<count;column++) regions[column]=new ImageCopy
                    {
                        SrcSubresource=new ImageSubresourceLayers(aspect,sourceLevel,sourceLayer,1),
                        DstSubresource=new ImageSubresourceLayers(aspect,0,0,1),
                        SrcOffset=new Offset3D(map.SignX>0?firstX:firstX-column,minY,0), DstOffset=new Offset3D(column,0,0),
                        Extent=new Extent3D(map.SignX>0?(uint)w:1u,(uint)h,1),
                    };
                    _context.Api.CmdCopyImage(command,source.Image,ImageLayout.TransferSrcOptimal,scratch.Image,ImageLayout.TransferDstOptimal,count,regions);
                    _textures.Require(_barriers,command,scratch,ResourceUsage.TransferSrc);
                    _textures.Require(_barriers,command,destination,ResourceUsage.TransferDst); _barriers.Flush(command);
                    count=map.SignY>0?1u:(uint)h;
                    for(int row=0;row<count;row++) regions[row]=new ImageCopy
                    {
                        SrcSubresource=new ImageSubresourceLayers(aspect,0,0,1),
                        DstSubresource=new ImageSubresourceLayers(aspect,destinationLevel,destinationLayer,1),
                        SrcOffset=new Offset3D(0,map.SignY>0?0:h-1-row,0), DstOffset=new Offset3D((int)x,(int)y+row,0),
                        Extent=new Extent3D((uint)w,map.SignY>0?(uint)h:1u,1),
                    };
                    _context.Api.CmdCopyImage(command,scratch.Image,ImageLayout.TransferSrcOptimal,destination.Image,ImageLayout.TransferDstOptimal,count,regions);
                }
            }
            finally { if(!_frameActive) _uploads.EndRecording(); }
            if(!_frameActive) _uploads.SubmitStandalone();
        }
        finally { if(scratchId!=0) DeleteTexture(scratchId); }
    }
}
