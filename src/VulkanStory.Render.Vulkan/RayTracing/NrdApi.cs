using System.Runtime.InteropServices;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Render.Vulkan.RayTracing;

internal static unsafe class NrdApi
{
    private const string Library = "VulkanStoryNrd";
    static NrdApi() => VulkanNativeLibrary.EnsureRegistered();
    [StructLayout(LayoutKind.Sequential)]
    internal struct Info
    {
        internal uint Pipelines, Permanent, Transient, Sets, Textures, Storage;
        internal uint TextureOffset, StorageOffset, ConstantOffset, SamplerOffset;
        internal uint ConstantSet, ResourceSet, ConstantBytes;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Pipeline
    {
        internal nint Shader;
        internal ulong Bytes;
        internal uint Textures, Storage, Constants;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Dispatch
    {
        internal nint Constants;
        internal uint Bytes, Pipeline, Width, Height, Resources;
    }
    [DllImport(Library, EntryPoint = "VsNrdCreate", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Create(out nint instance, out Info info);
    [DllImport(Library, EntryPoint = "VsNrdDestroy", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Destroy(nint instance);
    [DllImport(Library, EntryPoint = "VsNrdPipeline", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetPipeline(nint instance, uint index, out Pipeline pipeline);
    [DllImport(Library, EntryPoint = "VsNrdTexture", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetTexture(nint instance, uint permanent, uint index, out uint format, out uint factor);
    [DllImport(Library, EntryPoint = "VsNrdBegin", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Begin(nint instance, float* projection, float* previousProjection,
        float* view, float* previousView, uint width, uint height, float dx, float dy, float dz,
        float jitterX, float jitterY, float milliseconds, float range, uint reset, out uint count);
    [DllImport(Library, EntryPoint = "VsNrdDispatch", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetDispatch(nint instance, uint index, out Dispatch dispatch);
    [DllImport(Library, EntryPoint = "VsNrdResource", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetResource(nint instance, uint dispatch, uint index, out uint kind, out uint pool, out uint storage);
    internal static void Check(int result, string operation)
    {
        if (result != 0) throw new InvalidOperationException(operation + " failed: " + result);
    }
}
