using System.Runtime.InteropServices;
using System.Numerics;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Shaders;

namespace VulkanStory.Render.Vulkan.RayTracing;

/// <summary>Persistent GPU geometry and per-frame-slot top-level acceleration structures.</summary>
internal sealed unsafe class RayTracingScene : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct InstanceRecord
    {
        internal float M00, M01, M02, M03, M10, M11, M12, M13, M20, M21, M22, M23;
        internal uint IndexAndMask, SbtAndFlags;
        internal ulong AccelerationAddress;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct GeometryRecord
    {
        internal ulong Vertices, Indices, Identity;
        internal uint AtlasPage, Surface, LodLevel, TextureFlags;
    }

    private sealed class FrameScene : IDisposable
    {
        internal VulkanBuffer? Instances, Geometry;
        internal RayAccelerationStructure? Top;
        internal uint Capacity;
        internal uint BuiltInstanceCount;
        internal ulong BuildFrame = ulong.MaxValue;
        internal AccelerationStructureKHR Handle => Top?.Handle ?? default;
        public void Dispose()
        {
            Top?.Dispose(); Top = null;
            Geometry?.Dispose(); Geometry = null;
            Instances?.Dispose(); Instances = null;
        }
    }

    private sealed class Mesh : IDisposable
    {
        internal readonly VulkanBuffer Vertices, Indices;
        internal readonly uint VertexCount, PrimitiveCount;
        internal double X, Y, Z;
        internal readonly ulong World;
        internal int Dimension, AtlasPage, LodLevel;
        internal readonly RayTracingSurface Surface;
        internal byte VisibilityMask;
        internal readonly bool Dynamic;
        internal bool BottomDirty = true;
        internal ulong UploadFrame = ulong.MaxValue;
        internal VulkanBuffer? SkinInput, SkinJoints;
        internal long SkinVersion = long.MinValue;
        internal uint SkinJointCount;
        internal bool SkinPending, SkinReset;
        internal Vector3 PreviousOriginDelta;
        internal RayAccelerationStructure? Bottom;
        internal Mesh(VulkanContext context, FrameRing frames, UploadManager uploads, RayTracingGeometryUpload source, bool dynamic = false)
        {
            VertexCount = checked((uint)source.Vertices.Length);
            Dynamic = dynamic;
            PrimitiveCount = checked((uint)(source.Indices.Length / 3));
            X = source.OriginX; Y = source.OriginY; Z = source.OriginZ;
            World = source.WorldIdentity;
            VisibilityMask = source.VisibilityMask;
            Dimension = source.Dimension; AtlasPage = source.AtlasPage; Surface = source.Surface; LodLevel = source.LodLevel;
            BufferUsageFlags usage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit |
                BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr;
            Vertices = new VulkanBuffer(context, checked((ulong)source.Vertices.Length * (ulong)sizeof(RayTracingVertex)),
                usage, MemoryPropertyFlags.DeviceLocalBit, MemoryPoolClass.DeviceBuffers);
            try
            {
                Indices = new VulkanBuffer(context, checked((ulong)source.Indices.Length * sizeof(uint)),
                    usage, MemoryPropertyFlags.DeviceLocalBit, MemoryPoolClass.DeviceBuffers);
                fixed (RayTracingVertex* vertices = source.Vertices)
                    uploads.UploadToBuffer(Vertices, 0, (nint)vertices, Vertices.Size);
                fixed (uint* indices = source.Indices)
                    uploads.UploadToBuffer(Indices, 0, (nint)indices, Indices.Size);
            }
            catch
            {
                if (Indices != null) frames.DeferDeletion(Indices);
                frames.DeferDeletion(Vertices);
                throw;
            }
        }
        internal void Update(UploadManager uploads, RayTracingGeometryUpload source, ulong frame)
        {
            fixed (RayTracingVertex* vertices = source.Vertices)
                uploads.UploadToBuffer(Vertices, 0, (nint)vertices, Vertices.Size);
            SkinPending = false;
            Placement(source, frame);
        }
        private void Placement(RayTracingGeometryUpload source, ulong frame)
        {
            X = source.OriginX; Y = source.OriginY; Z = source.OriginZ;
            Dimension = source.Dimension; AtlasPage = source.AtlasPage; LodLevel = source.LodLevel;
            VisibilityMask = source.VisibilityMask; BottomDirty = true; UploadFrame = frame;
        }
        internal void PrepareSkin(VulkanContext context, FrameRing frames, UploadManager uploads,
            in RayTracingSkinUpload source, ulong frame)
        {
            BufferUsageFlags usage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.ShaderDeviceAddressBit;
            if (SkinInput == null)
                SkinInput = new VulkanBuffer(context, Vertices.Size, usage, MemoryPropertyFlags.DeviceLocalBit);
            if (SkinVersion != source.GeometryVersion)
            {
                fixed (RayTracingVertex* vertices = source.Geometry.Vertices)
                    uploads.UploadToBuffer(SkinInput, 0, (nint)vertices, SkinInput.Size);
                SkinVersion = source.GeometryVersion;
            }
            ulong bytes = checked((ulong)source.Joints.Length * (ulong)sizeof(RayTracingJoint));
            if (SkinJoints == null || SkinJoints.Size != bytes)
            {
                if (SkinJoints != null) frames.DeferDeletion(SkinJoints);
                SkinJoints = new VulkanBuffer(context, bytes, usage, MemoryPropertyFlags.DeviceLocalBit);
            }
            fixed (RayTracingJoint* joints = source.Joints)
                uploads.UploadToBuffer(SkinJoints, 0, (nint)joints, bytes);
            SkinJointCount = (uint)source.Joints.Length; SkinReset = source.Reset;
            PreviousOriginDelta = source.PreviousOriginDelta; SkinPending = true;
            Placement(source.Geometry, frame);
        }
        public void Dispose()
        {
            Bottom?.Dispose(); Bottom = null;
            SkinInput?.Dispose(); SkinJoints?.Dispose();
            Indices.Dispose(); Vertices.Dispose();
        }
    }

    private sealed class DynamicMesh : IDisposable
    {
        internal readonly Mesh?[] Slots;
        internal readonly uint[] Topology;
        internal readonly int VertexCount;
        internal readonly ulong World;
        internal readonly RayTracingSurface Surface;
        internal DynamicMesh(RayTracingGeometryUpload source, int frames)
        {
            Slots = new Mesh?[frames]; Topology = (uint[])source.Indices.Clone();
            VertexCount = source.Vertices.Length; World = source.WorldIdentity; Surface = source.Surface;
        }
        internal bool Matches(RayTracingGeometryUpload source) => VertexCount == source.Vertices.Length &&
            World == source.WorldIdentity && Surface == source.Surface && Topology.AsSpan().SequenceEqual(source.Indices);
        public void Dispose() { foreach (var slot in Slots) slot?.Dispose(); }
    }

    private readonly VulkanContext context;
    private readonly FrameRing frames;
    private readonly UploadManager uploads;
    private readonly ShaderCompiler compiler;
    private RaySkinningPipeline? skinning;
    private readonly KhrAccelerationStructure api;
    private readonly PhysicalDeviceAccelerationStructurePropertiesKHR limits;
    private readonly Dictionary<ulong, Mesh> meshes = new();
    private readonly Dictionary<ulong, DynamicMesh> dynamicMeshes = new();
    private readonly FrameScene[] scenes;
    private readonly List<KeyValuePair<ulong, Mesh>> selected = new();
    private bool disposed;
    internal int GeometryCount => meshes.Count;
    internal uint LastInstanceCount { get; private set; }
    internal AccelerationStructureKHR TopLevel => scenes[frames.Current.Index].Handle;
    internal VulkanBuffer? GeometryBuffer => scenes[frames.Current.Index].Geometry;

    internal RayTracingScene(VulkanContext context, FrameRing frames, UploadManager uploads, ShaderCompiler compiler)
    {
        this.context = context; this.frames = frames; this.uploads = uploads; this.compiler = compiler;
        api = context.AccelerationStructures;
        var properties = new PhysicalDeviceAccelerationStructurePropertiesKHR
        {
            SType = StructureType.PhysicalDeviceAccelerationStructurePropertiesKhr,
        };
        var query = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &properties };
        context.Api.GetPhysicalDeviceProperties2(context.PhysicalDevice, &query);
        limits = properties;
        scenes = Enumerable.Range(0, frames.FramesInFlight).Select(_ => new FrameScene()).ToArray();
    }

    internal void Set(ulong identity, RayTracingGeometryUpload source)
    {
        if (!Validate(identity, source)) { Remove(identity); return; }
        var replacement = new Mesh(context, frames, uploads, source);
        Remove(identity);
        meshes[identity] = replacement;
    }

    internal void SetDynamic(ulong identity, RayTracingGeometryUpload source, ulong frame)
    {
        if (!Validate(identity, source)) { Remove(identity); return; }
        DynamicSlot(identity, source, frame, updateVertices: true);
    }

    internal void SetSkinned(ulong identity, in RayTracingSkinUpload source, ulong frame)
    {
        if (!Validate(identity, source.Geometry)) { Remove(identity); return; }
        if (source.Joints.Length == 0) throw new ArgumentException("A skin palette must contain joints.");
        foreach (var vertex in source.Geometry.Vertices)
            if (vertex.JointIndex >= source.Joints.Length) throw new ArgumentException("Skin vertex exceeds the palette.");
        foreach (float value in MemoryMarshal.Cast<RayTracingJoint, float>(source.Joints.AsSpan()))
            if (!float.IsFinite(value)) throw new ArgumentException("Skin palette contains a non-finite transform.");
        Mesh slot = DynamicSlot(identity, source.Geometry, frame, updateVertices: false);
        slot.PrepareSkin(context, frames, uploads, source, frame);
    }

    private Mesh DynamicSlot(ulong identity, RayTracingGeometryUpload source, ulong frame, bool updateVertices)
    {
        if (!dynamicMeshes.TryGetValue(identity, out var owner) || !owner.Matches(source))
        {
            Remove(identity);
            owner = new DynamicMesh(source, frames.FramesInFlight);
            dynamicMeshes.Add(identity, owner);
        }
        int index = frames.Current.Index;
        Mesh? slot = owner.Slots[index];
        if (slot != null && slot.UploadFrame == frame)
        {
            // Recorded commands may already refer to this slot's old inputs.
            frames.DeferDeletion(slot); owner.Slots[index] = null; slot = null;
        }
        if (slot == null)
        {
            slot = new Mesh(context, frames, uploads, source, dynamic: true) { UploadFrame = frame };
            owner.Slots[index] = slot;
        }
        else if (updateVertices) slot.Update(uploads, source, frame);
        meshes[identity] = slot;
        return slot;
    }

    private bool Validate(ulong identity, RayTracingGeometryUpload source)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (identity == 0 || source.WorldIdentity == 0) throw new ArgumentOutOfRangeException(nameof(identity));
        if (source.Vertices.Length == 0 || source.Indices.Length == 0)
            return false;
        if (source.Indices.Length % 3 != 0) throw new ArgumentException("Ray geometry must contain complete triangles.");
        if ((ulong)(source.Indices.Length / 3) > limits.MaxPrimitiveCount)
            throw new ArgumentException("Ray geometry exceeds the selected device's primitive limit.");
        if (source.AtlasPage < 0 || !double.IsFinite(source.OriginX) || !double.IsFinite(source.OriginY) || !double.IsFinite(source.OriginZ))
            throw new ArgumentException("Ray geometry has invalid placement or material metadata.");
        foreach (uint index in source.Indices)
            if (index >= source.Vertices.Length) throw new ArgumentException("Ray geometry index exceeds the vertex range.");
        foreach (RayTracingVertex vertex in source.Vertices)
            if (!float.IsFinite(vertex.Position.X) || !float.IsFinite(vertex.Position.Y) || !float.IsFinite(vertex.Position.Z))
                throw new ArgumentException("Ray geometry contains a non-finite vertex position.");
        return true;
    }

    internal void Remove(ulong identity)
    {
        if (dynamicMeshes.Remove(identity, out var owner))
        { meshes.Remove(identity); frames.DeferDeletion(owner); }
        else if (meshes.Remove(identity, out Mesh? mesh)) frames.DeferDeletion(mesh);
    }

    internal void Clear()
    {
        foreach (ulong identity in meshes.Keys.ToArray()) Remove(identity);
        meshes.Clear(); selected.Clear(); LastInstanceCount = 0;
        for (int i = 0; i < scenes.Length; i++)
        {
            frames.DeferDeletion(scenes[i]);
            scenes[i] = new FrameScene();
        }
    }

    internal void RemoveWorld(ulong identity)
    {
        foreach (ulong key in meshes.Where(entry => entry.Value.World == identity).Select(entry => entry.Key).ToArray())
            Remove(key);
    }

    internal bool MapAtlasSlots(ReadOnlySpan<uint> slots, ReadOnlySpan<uint> flags)
    {
        foreach (var entry in selected)
            if (entry.Value.AtlasPage >= slots.Length || entry.Value.AtlasPage >= flags.Length || slots[entry.Value.AtlasPage] == 0)
                return false;
        var data = (GeometryRecord*)scenes[frames.Current.Index].Geometry!.Mapped;
        for (int i = 0; i < selected.Count; i++)
        {
            int page = selected[i].Value.AtlasPage;
            data[i].AtlasPage = slots[page];
            data[i].TextureFlags = flags[page];
        }
        return true;
    }

    /// <summary>Records real geometry builds followed by a camera-relative TLAS; the acquired slot has completed its prior GPU use.</summary>
    internal bool Build(CommandBuffer commands, ulong frameIdentity, ulong worldIdentity, double cameraX, double cameraY, double cameraZ, int dimension)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!double.IsFinite(cameraX) || !double.IsFinite(cameraY) || !double.IsFinite(cameraZ))
            throw new ArgumentException("Ray scene camera origin must be finite.");
        selected.Clear();
        foreach (var entry in meshes)
            if (entry.Value.World == worldIdentity && entry.Value.Dimension == dimension) selected.Add(entry);
        LastInstanceCount = checked((uint)selected.Count);
        if (LastInstanceCount == 0) return false;
        if ((ulong)LastInstanceCount > limits.MaxInstanceCount || LastInstanceCount > 0x1000000u)
            throw new InvalidOperationException("Ray scene exceeds the selected device's instance limit.");

        bool built = false;
        foreach (var entry in selected)
        {
            Mesh mesh = entry.Value;
            if (!mesh.BottomDirty) continue;
            if (mesh.SkinPending)
            {
                skinning ??= new RaySkinningPipeline(context, compiler);
                RayAccelerationStructure.Barrier(context, commands,
                    PipelineStageFlags2.CopyBit | PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.RayTracingShaderBitKhr | PipelineStageFlags2.AccelerationStructureBuildBitKhr,
                    AccessFlags2.TransferWriteBit | AccessFlags2.ShaderStorageWriteBit | AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderReadBit,
                    PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
                skinning.Record(commands, mesh.SkinInput!, mesh.Vertices, mesh.SkinJoints!, mesh.VertexCount,
                    mesh.SkinJointCount, mesh.SkinReset, mesh.PreviousOriginDelta);
                uploads.NoteUse(commands, mesh.SkinInput!); uploads.NoteUse(commands, mesh.SkinJoints!);
                RayAccelerationStructure.Barrier(context, commands, PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageWriteBit,
                    PipelineStageFlags2.AccelerationStructureBuildBitKhr | PipelineStageFlags2.RayTracingShaderBitKhr,
                    AccessFlags2.ShaderReadBit | AccessFlags2.ShaderStorageReadBit);
                mesh.SkinPending = false;
            }
            var geometry = Triangles(mesh);
            var info = new AccelerationStructureBuildGeometryInfoKHR
            {
                SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
                Type = AccelerationStructureTypeKHR.BottomLevelKhr,
                Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr |
                    (mesh.Dynamic ? BuildAccelerationStructureFlagsKHR.AllowUpdateBitKhr : 0),
                Mode = mesh.Bottom != null ? BuildAccelerationStructureModeKHR.UpdateKhr : BuildAccelerationStructureModeKHR.BuildKhr,
                SrcAccelerationStructure = mesh.Bottom?.Handle ?? default,
                GeometryCount = 1, PGeometries = &geometry,
            };
            if (mesh.Bottom == null)
            {
                var sizes = RayAccelerationStructure.Sizes(context, api, info, mesh.PrimitiveCount);
                mesh.Bottom = new RayAccelerationStructure(context, api, info.Type, sizes, limits.MinAccelerationStructureScratchOffsetAlignment);
            }
            else RayAccelerationStructure.Barrier(context, commands,
                PipelineStageFlags2.AccelerationStructureBuildBitKhr | PipelineStageFlags2.RayTracingShaderBitKhr,
                AccessFlags2.AccelerationStructureReadBitKhr | AccessFlags2.AccelerationStructureWriteBitKhr,
                PipelineStageFlags2.AccelerationStructureBuildBitKhr,
                AccessFlags2.AccelerationStructureReadBitKhr | AccessFlags2.AccelerationStructureWriteBitKhr);
            mesh.Bottom.Build(commands, info, new AccelerationStructureBuildRangeInfoKHR { PrimitiveCount = mesh.PrimitiveCount });
            uploads.NoteUse(commands, mesh.Vertices); uploads.NoteUse(commands, mesh.Indices);
            if (!mesh.Dynamic) mesh.Bottom.RetireBuildWorkspace(frames);
            mesh.BottomDirty = false;
            built = true;
        }
        if (built)
            RayAccelerationStructure.Barrier(context, commands, PipelineStageFlags2.AccelerationStructureBuildBitKhr,
                AccessFlags2.AccelerationStructureWriteBitKhr, PipelineStageFlags2.AccelerationStructureBuildBitKhr,
                AccessFlags2.AccelerationStructureReadBitKhr);

        int slotIndex = frames.Current.Index;
        FrameScene slot = scenes[slotIndex];
        if (slot.BuildFrame == frameIdentity)
        {
            frames.DeferDeletion(slot);
            slot = scenes[slotIndex] = new FrameScene();
        }
        slot.BuildFrame = frameIdentity;
        EnsureSlot(slot, LastInstanceCount);
        var instanceData = (InstanceRecord*)slot.Instances!.Mapped;
        var geometryData = (GeometryRecord*)slot.Geometry!.Mapped;
        for (int index = 0; index < selected.Count; index++)
        {
            var entry = selected[index]; Mesh mesh = entry.Value;
            instanceData[index] = new InstanceRecord
            {
                M00 = 1, M11 = 1, M22 = 1,
                M03 = checked((float)(mesh.X - cameraX)), M13 = checked((float)(mesh.Y - cameraY)), M23 = checked((float)(mesh.Z - cameraZ)),
                IndexAndMask = (uint)index | ((uint)mesh.VisibilityMask << 24),
                // Double-sided triangles; material alpha filtering belongs to traversal.
                SbtAndFlags = 1u << 24,
                AccelerationAddress = mesh.Bottom!.Address,
            };
            geometryData[index] = new GeometryRecord
            {
                Vertices = RayAccelerationStructure.BufferAddress(context, mesh.Vertices),
                Indices = RayAccelerationStructure.BufferAddress(context, mesh.Indices),
                Identity = entry.Key, AtlasPage = (uint)mesh.AtlasPage, Surface = (uint)mesh.Surface,
                LodLevel = (uint)mesh.LodLevel,
            };
            uploads.NoteUse(commands, mesh.Vertices); uploads.NoteUse(commands, mesh.Indices);
        }
        RayAccelerationStructure.Barrier(context, commands, PipelineStageFlags2.HostBit, AccessFlags2.HostWriteBit,
            PipelineStageFlags2.AccelerationStructureBuildBitKhr | PipelineStageFlags2.RayTracingShaderBitKhr,
            AccessFlags2.ShaderReadBit);
        var instances = new AccelerationStructureGeometryKHR
        {
            SType = StructureType.AccelerationStructureGeometryKhr,
            GeometryType = GeometryTypeKHR.InstancesKhr,
            Geometry = new AccelerationStructureGeometryDataKHR
            {
                Instances = new AccelerationStructureGeometryInstancesDataKHR
                {
                    SType = StructureType.AccelerationStructureGeometryInstancesDataKhr,
                    Data = new DeviceOrHostAddressConstKHR { DeviceAddress = RayAccelerationStructure.BufferAddress(context, slot.Instances) },
                },
            },
        };
        var topInfo = new AccelerationStructureBuildGeometryInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
            Type = AccelerationStructureTypeKHR.TopLevelKhr,
            Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr | BuildAccelerationStructureFlagsKHR.AllowUpdateBitKhr,
            Mode = slot.Top != null && slot.BuiltInstanceCount == LastInstanceCount
                ? BuildAccelerationStructureModeKHR.UpdateKhr : BuildAccelerationStructureModeKHR.BuildKhr,
            SrcAccelerationStructure = slot.Top != null && slot.BuiltInstanceCount == LastInstanceCount ? slot.Top.Handle : default,
            GeometryCount = 1, PGeometries = &instances,
        };
        if (slot.Top == null)
        {
            var sizes = RayAccelerationStructure.Sizes(context, api, topInfo, slot.Capacity);
            slot.Top = new RayAccelerationStructure(context, api, topInfo.Type, sizes, limits.MinAccelerationStructureScratchOffsetAlignment);
        }
        // A reusable slot still needs a device memory dependency before modifying its AS.
        RayAccelerationStructure.Barrier(context, commands,
            PipelineStageFlags2.AccelerationStructureBuildBitKhr | PipelineStageFlags2.RayTracingShaderBitKhr | PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.AccelerationStructureWriteBitKhr | AccessFlags2.AccelerationStructureReadBitKhr,
            PipelineStageFlags2.AccelerationStructureBuildBitKhr,
            AccessFlags2.AccelerationStructureReadBitKhr | AccessFlags2.AccelerationStructureWriteBitKhr);
        slot.Top.Build(commands, topInfo, new AccelerationStructureBuildRangeInfoKHR { PrimitiveCount = LastInstanceCount });
        slot.BuiltInstanceCount = LastInstanceCount;
        uploads.NoteUse(commands, slot.Instances); uploads.NoteUse(commands, slot.Geometry);
        RayAccelerationStructure.Barrier(context, commands, PipelineStageFlags2.AccelerationStructureBuildBitKhr,
            AccessFlags2.AccelerationStructureWriteBitKhr, PipelineStageFlags2.RayTracingShaderBitKhr | PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.AccelerationStructureReadBitKhr);
        return true;
    }

    private AccelerationStructureGeometryKHR Triangles(Mesh mesh) => new()
    {
        SType = StructureType.AccelerationStructureGeometryKhr,
        GeometryType = GeometryTypeKHR.TrianglesKhr,
        // Transparent attenuation must run once per triangle, even if traversal revisits it.
        Flags = GeometryFlagsKHR.NoDuplicateAnyHitInvocationBitKhr,
        // TODO(RT): mark verified opaque materials OpaqueBitKhr after adding an opaque shadow closest-hit shader.
        Geometry = new AccelerationStructureGeometryDataKHR
        {
            Triangles = new AccelerationStructureGeometryTrianglesDataKHR
            {
                SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr,
                VertexFormat = Format.R32G32B32Sfloat,
                VertexData = new DeviceOrHostAddressConstKHR { DeviceAddress = RayAccelerationStructure.BufferAddress(context, mesh.Vertices) },
                VertexStride = (ulong)sizeof(RayTracingVertex), MaxVertex = mesh.VertexCount - 1,
                IndexType = IndexType.Uint32,
                IndexData = new DeviceOrHostAddressConstKHR { DeviceAddress = RayAccelerationStructure.BufferAddress(context, mesh.Indices) },
            },
        },
    };

    private void EnsureSlot(FrameScene slot, uint count)
    {
        if (slot.Capacity >= count) return;
        uint capacity = count;
        var instances = new VulkanBuffer(context, checked((ulong)capacity * (ulong)sizeof(InstanceRecord)),
            BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPoolClass.DeviceBuffers);
        VulkanBuffer geometry;
        try
        {
            geometry = new VulkanBuffer(context, checked((ulong)capacity * (ulong)sizeof(GeometryRecord)),
                BufferUsageFlags.StorageBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                MemoryPoolClass.DeviceBuffers);
        }
        catch { instances.Dispose(); throw; }
        slot.Top?.Dispose(); slot.Top = null; slot.BuiltInstanceCount = 0;
        slot.Geometry?.Dispose(); slot.Instances?.Dispose();
        slot.Instances = instances; slot.Geometry = geometry; slot.Capacity = capacity;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (FrameScene slot in scenes) slot.Dispose();
        skinning?.Dispose(); skinning = null;
        foreach (var entry in meshes) if (!dynamicMeshes.ContainsKey(entry.Key)) entry.Value.Dispose();
        foreach (var owner in dynamicMeshes.Values) owner.Dispose();
        dynamicMeshes.Clear();
        meshes.Clear(); selected.Clear();
    }
}
