using Silk.NET.Vulkan;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    private int glStencilTransferProgram;
    private readonly record struct StencilTile(int X, int Y, int W, int H, int SX, int SY, int SW, int SH);

    private ComputeProgram StencilTransferProgram()
    {
        if (_compute.Get(glStencilTransferProgram) is { } existing) return existing;
        const string code = """
            #version 450
            layout(local_size_x=64) in;
            layout(set=0,binding=0,std430) readonly buffer Source { uint words[]; } src;
            layout(set=0,binding=1,std430) writeonly buffer Destination { uint words[]; } dst;
            layout(set=0,binding=2,std430) readonly buffer Axes { uint index[]; } axis;
            layout(push_constant) uniform Parameters { ivec4 size; } p;
            void main() {
                uint word=gl_GlobalInvocationID.x, count=uint(p.size.z*p.size.w);
                if(word >= (count+3u)/4u) return;
                uint wordValue=0u;
                for(uint lane=0u;lane<4u;lane++) {
                    uint pixel=word*4u+lane;
                    if(pixel>=count) break;
                    ivec2 d=ivec2(pixel%uint(p.size.z),pixel/uint(p.size.z));
                    ivec2 s=ivec2(axis.index[d.x],axis.index[p.size.z+d.y]);
                    uint index=uint(s.y*p.size.x+s.x);
                    uint value=(src.words[index/4u] >> ((index%4u)*8u)) & 255u;
                    wordValue |= value << (lane*8u);
                }
                dst.words[word]=wordValue;
            }
            """;
        glStencilTransferProgram = CreateComputeProgram(code, "gl-stencil-transfer",
            [new ComputeSlot(0, ComputeSlotKind.Buffer), new ComputeSlot(1, ComputeSlotKind.Buffer), new ComputeSlot(2, ComputeSlotKind.Buffer)], 16, 64, 1);
        return _compute.Get(glStencilTransferProgram) ?? throw new InvalidOperationException("Stencil transfer shader failed: " + GetError());
    }

    private void TransformGlStencil(VulkanTexture source, uint sourceLevel, uint sourceLayer, VulkanTexture destination,
        uint destinationLevel, uint destinationLayer, TextureBlitRectangle sourceRect, TextureBlitRectangle destinationRect,
        int sourceWidth, int sourceHeight, Rect2D clip)
    {
        if (!_frameActive)
        {
            RecordGlGraphics(() => TransformGlStencil(source, sourceLevel, sourceLayer, destination, destinationLevel, destinationLayer,
                sourceRect, destinationRect, sourceWidth, sourceHeight, clip));
            return;
        }
        EndNativePass(); _targets.EndPass(Commands); _targets.FlushAllPendingClears(Commands); _targets.EndRendering(Commands);
        long sourceDX=(long)sourceRect.X1-sourceRect.X0, sourceDY=(long)sourceRect.Y1-sourceRect.Y0;
        long destinationDX=(long)destinationRect.X1-destinationRect.X0, destinationDY=(long)destinationRect.Y1-destinationRect.Y0;
        double MapX(int x) => sourceRect.X0 + (x + .5 - destinationRect.X0) * sourceDX / destinationDX;
        double MapY(int y) => sourceRect.Y0 + (y + .5 - destinationRect.Y0) * sourceDY / destinationDY;
        var pending = new Stack<(int X, int Y, int W, int H)>();
        pending.Push((clip.Offset.X, clip.Offset.Y, (int)clip.Extent.Width, (int)clip.Extent.Height));
        var tiles = new List<StencilTile>();
        var limits=_context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice).Limits;
        ulong maxRange=limits.MaxStorageBufferRange, axisAlignment=Math.Max(4UL,limits.MinStorageBufferOffsetAlignment);
        ulong sourceBytes = 0, destinationBytes = 0;
        static ulong PackedBytes(int w, int h) => (checked((ulong)w * (ulong)h) + 3) & ~3UL;
        while (pending.TryPop(out var tile))
        {
            if (tile.W <= 256 && tile.H <= 256)
            {
                // At most 256 positions per axis: classify clipped pixels on
                // the CPU in double precision. Integer axis indices keep mirror
                // ties exact instead of subtracting rounded GPU float offsets.
                int x0=0, x1=tile.W, y0=0, y1=tile.H;
                bool ValidX(int x) => MapX(tile.X+x) >= 0 && MapX(tile.X+x) < sourceWidth;
                bool ValidY(int y) => MapY(tile.Y+y) >= 0 && MapY(tile.Y+y) < sourceHeight;
                while(x0<x1 && !ValidX(x0)) x0++;
                while(x1>x0 && !ValidX(x1-1)) x1--;
                while(y0<y1 && !ValidY(y0)) y0++;
                while(y1>y0 && !ValidY(y1-1)) y1--;
                if(x0==x1 || y0==y1) continue;
                tile=(tile.X+x0,tile.Y+y0,x1-x0,y1-y0);
                double firstX=MapX(tile.X), lastX=MapX(tile.X+tile.W-1), firstY=MapY(tile.Y), lastY=MapY(tile.Y+tile.H-1);
                int sx=(int)Math.Floor(Math.Min(firstX,lastX)), sy=(int)Math.Floor(Math.Min(firstY,lastY));
                int sw=(int)Math.Floor(Math.Max(firstX,lastX))-sx+1, sh=(int)Math.Floor(Math.Max(firstY,lastY))-sy+1;
                ulong sb=PackedBytes(sw,sh), db=PackedBytes(tile.W,tile.H);
                if(sb<=maxRange && db<=maxRange)
                {
                    tiles.Add(new StencilTile(tile.X,tile.Y,tile.W,tile.H,sx,sy,sw,sh));
                    sourceBytes=Math.Max(sourceBytes,sb); destinationBytes=Math.Max(destinationBytes,db);
                    continue;
                }
            }
            if(tile.W>=tile.H && tile.W>1)
            { int half=tile.W/2; pending.Push((tile.X,tile.Y,half,tile.H)); pending.Push((tile.X+half,tile.Y,tile.W-half,tile.H)); }
            else if(tile.H>1)
            { int half=tile.H/2; pending.Push((tile.X,tile.Y,tile.W,half)); pending.Push((tile.X,tile.Y+half,tile.W,tile.H-half)); }
            else throw new NotSupportedException("Stencil transfer exceeds the storage-buffer range.");
        }
        if(tiles.Count==0) return;
        ComputeProgram program=StencilTransferProgram();
        VulkanBuffer? src=null, dst=null, axes=null;
        try
        {
            src=new VulkanBuffer(_context,sourceBytes,BufferUsageFlags.TransferDstBit|BufferUsageFlags.StorageBufferBit,
                MemoryPropertyFlags.DeviceLocalBit,MemoryPoolClass.DeviceBuffers);
            dst=new VulkanBuffer(_context,destinationBytes,BufferUsageFlags.TransferSrcBit|BufferUsageFlags.StorageBufferBit,
                MemoryPropertyFlags.DeviceLocalBit,MemoryPoolClass.DeviceBuffers);
            ulong axisBytes=0;
            static ulong Align(ulong value,ulong alignment) => ((value+alignment-1)/alignment)*alignment;
            foreach(StencilTile tile in tiles) axisBytes=Align(axisBytes,axisAlignment)+(ulong)(tile.W+tile.H)*4;
            axes=new VulkanBuffer(_context,axisBytes,BufferUsageFlags.StorageBufferBit,
                MemoryPropertyFlags.HostVisibleBit|MemoryPropertyFlags.HostCoherentBit,MemoryPoolClass.Staging);
            Pipeline pipeline=_compute.PipelineFor(program,Array.Empty<uint>());
            Vk api=_context.Api; CommandBuffer command=Commands;
            api.CmdBindPipeline(command,PipelineBindPoint.Compute,pipeline);
            bool reused=false;
            uint* push=stackalloc uint[4];
            ulong axisOffset=0;
            foreach(StencilTile tile in tiles)
            {
                axisOffset=Align(axisOffset,axisAlignment);
                uint* indices=(uint*)((byte*)axes.Mapped+axisOffset);
                for(int x=0;x<tile.W;x++) indices[x]=(uint)Math.Clamp((int)Math.Floor(MapX(tile.X+x))-tile.SX,0,tile.SW-1);
                for(int y=0;y<tile.H;y++) indices[tile.W+y]=(uint)Math.Clamp((int)Math.Floor(MapY(tile.Y+y))-tile.SY,0,tile.SH-1);
                DescriptorSet set=_computeArenas[_frames.Current.Index].GetBuffers(program.SetLayout,
                    [new DescriptorBufferInfo(src.Handle,0,src.Size),new DescriptorBufferInfo(dst.Handle,0,dst.Size),
                     new DescriptorBufferInfo(axes.Handle,axisOffset,(ulong)(tile.W+tile.H)*4)]);
                api.CmdBindDescriptorSets(command,PipelineBindPoint.Compute,program.Layout,0,1,&set,0,null);
                axisOffset+=(ulong)(tile.W+tile.H)*4;
                if(reused)
                {
                    StencilBufferBarrier(command,src,PipelineStageFlags2.ComputeShaderBit|PipelineStageFlags2.CopyBit,
                        AccessFlags2.ShaderStorageReadBit|AccessFlags2.TransferWriteBit,PipelineStageFlags2.CopyBit,AccessFlags2.TransferWriteBit);
                    StencilBufferBarrier(command,dst,PipelineStageFlags2.ComputeShaderBit|PipelineStageFlags2.CopyBit,
                        AccessFlags2.ShaderStorageWriteBit|AccessFlags2.TransferReadBit,PipelineStageFlags2.ComputeShaderBit,AccessFlags2.ShaderStorageWriteBit);
                }
                _textures.Require(_barriers,command,source,ResourceUsage.TransferSrc); _barriers.Flush(command);
                var read=new BufferImageCopy { ImageSubresource=new ImageSubresourceLayers(ImageAspectFlags.StencilBit,sourceLevel,sourceLayer,1),
                    ImageOffset=new Offset3D(tile.SX,tile.SY,0),ImageExtent=new Extent3D((uint)tile.SW,(uint)tile.SH,1) };
                api.CmdCopyImageToBuffer(command,source.Image,ImageLayout.TransferSrcOptimal,src.Handle,1,&read);
                StencilBufferBarrier(command,src,PipelineStageFlags2.CopyBit,AccessFlags2.TransferWriteBit,
                    PipelineStageFlags2.ComputeShaderBit,AccessFlags2.ShaderStorageReadBit);
                push[0]=(uint)tile.SW; push[1]=(uint)tile.SH; push[2]=(uint)tile.W; push[3]=(uint)tile.H;
                api.CmdPushConstants(command,program.Layout,ShaderStageFlags.ComputeBit,0,16,push);
                api.CmdDispatch(command,(uint)(((tile.W*tile.H+3)/4+63)/64),1,1); _graph.NoteDispatch();
                StencilBufferBarrier(command,dst,PipelineStageFlags2.ComputeShaderBit,AccessFlags2.ShaderStorageWriteBit,
                    PipelineStageFlags2.CopyBit,AccessFlags2.TransferReadBit);
                _textures.Require(_barriers,command,destination,ResourceUsage.TransferDst); _barriers.Flush(command);
                var write=new BufferImageCopy { ImageSubresource=new ImageSubresourceLayers(ImageAspectFlags.StencilBit,destinationLevel,destinationLayer,1),
                    ImageOffset=new Offset3D(tile.X,tile.Y,0),ImageExtent=new Extent3D((uint)tile.W,(uint)tile.H,1) };
                api.CmdCopyBufferToImage(command,dst.Handle,destination.Image,ImageLayout.TransferDstOptimal,1,&write);
                reused=true;
            }
        }
        finally { if(src is not null) _frames.DeferDeletion(src); if(dst is not null) _frames.DeferDeletion(dst); if(axes is not null) _frames.DeferDeletion(axes); }
    }

    private void StencilBufferBarrier(CommandBuffer command,VulkanBuffer buffer,PipelineStageFlags2 srcStage,AccessFlags2 srcAccess,
        PipelineStageFlags2 dstStage,AccessFlags2 dstAccess)
    {
        var barrier=new BufferMemoryBarrier2 { SType=StructureType.BufferMemoryBarrier2,SrcStageMask=srcStage,SrcAccessMask=srcAccess,
            DstStageMask=dstStage,DstAccessMask=dstAccess,SrcQueueFamilyIndex=Vk.QueueFamilyIgnored,DstQueueFamilyIndex=Vk.QueueFamilyIgnored,
            Buffer=buffer.Handle,Offset=0,Size=buffer.Size };
        var dependency=new DependencyInfo { SType=StructureType.DependencyInfo,BufferMemoryBarrierCount=1,PBufferMemoryBarriers=&barrier };
        _context.Api.CmdPipelineBarrier2(command,&dependency);
    }
}
