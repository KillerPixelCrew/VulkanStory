using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;
using VulkanStory.Render.Vulkan.Shaders;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    private sealed class WorldGeometry(VulkanBuffer buffer, byte[] parameters, uint frame)
    {
        internal readonly VulkanBuffer Buffer = buffer;
        internal readonly byte[] Parameters = parameters;
        internal uint Frame = frame;
    }
    private readonly Dictionary<(ulong Source, int Hash), List<WorldGeometry>> worldGeometry = new();
    private readonly List<(ulong Source, int Hash)> oldWorldGeometry = new();
    private ulong worldGeometryBytes;
    private int worldGeometryProgram;
    internal long WorldGeometryDispatchesRecorded { get; private set; }
    internal long WorldGeometryCacheReuses { get; private set; }
    private static readonly string[] WorldWarpNames = ["timeCounter", "windWaveCounter", "windWaveCounterHighFreq", "waterWaveCounter",
        "windSpeed", "globalWarpIntensity", "glitchWaviness", "windWaveIntensity", "waterWaveIntensity", "perceptionEffectIntensity"];
    private static readonly string[] PreviousWorldWarpNames = Array.ConvertAll(WorldWarpNames,
        name => "prev" + char.ToUpperInvariant(name[0]) + name[1..]);

    private static void SetWorldDeformationMode(ShaderProgramResources program, bool enabled)
    {
        int location = program.LocationOf("vsComputeWorldDeformation");
        if (location < 0) return;
        int value = enabled ? 1 : 0;
        ReadOnlySpan<byte> bytes = new(&value, 4);
        if (ShaderProgramResources.IsPushLocation(location)) program.SetPushUniform(location, bytes);
        else program.SetUniform(location, bytes);
    }

    private bool TryPrepareWorldGeometry(ShaderProgramResources program, int meshId)
    {
        // The shadow program deliberately has no world-warp contract: stock shadows
        // consume local joint geometry with their original model-view transform.
        if (!program.ComputeWarpCompatible || program.LocationOf("vsComputeWorldDeformation") < 0 || program.WorldWarpWaving is not (0 or 1) ||
            _drawDeformation is not { } local || _meshes.Get(meshId) is not { } mesh) return false;
        VertexAttribute flags = default;
        foreach (var attribute in mesh.Layout.Attributes)
            if (attribute.Location == 3 && attribute.Format == Format.R32Sint) flags = attribute;
        if (flags.Format != Format.R32Sint || flags.Binding >= mesh.BindingOrder.Count || flags.Offset % 4 != 0) return false;
        uint stride = 0;
        foreach (var binding in mesh.Layout.Bindings)
            if (binding.Binding == flags.Binding && !binding.PerInstance) stride = binding.Stride;
        if (stride == 0 || stride % 4 != 0 || mesh.Buffers[mesh.BindingOrder[(int)flags.Binding]] is not { } flagBuffer ||
            (flagBuffer.Usage & BufferUsageFlags.StorageBufferBit) == 0) return false;
        uint count = (uint)(local.Size / 64);
        if (count == 0 || flagBuffer.Size < flags.Offset + (ulong)(count - 1) * stride + 4) return false;
        Span<byte> parameters = stackalloc byte[148];
        if (!ReadWorldParameters(program, false, count, stride, flags.Offset, parameters)) return false;
        var currentWorld = WarpGeometry(local, flagBuffer, parameters);
        if (currentWorld == null) return false;
        VulkanBuffer? previousWorld = null;
        if (_drawPreviousDeformation is { } previous)
        {
            if (!ReadWorldParameters(program, true, count, stride, flags.Offset, parameters)) return false;
            previousWorld = WarpGeometry(previous, flagBuffer, parameters);
            if (previousWorld == null) return false;
        }
        _drawDeformation = currentWorld; _drawPreviousDeformation = previousWorld;
        return true;
    }

    private bool ReadWorldParameters(ShaderProgramResources program, bool previous, uint count, uint stride,
        uint offset, Span<byte> output)
    {
        output.Clear();
        if (!CopyWorldUniform(program, previous ? "prevModelMatrix" : "modelMatrix", "mat4", output[..64]) ||
            !CopyWorldUniform(program, "addRenderFlags", "int", output.Slice(140, 4))) return false;
        if (!ReadGeometryWarpState(program, previous, output.Slice(64, 64))) return false;
        var words = MemoryMarshal.Cast<byte, uint>(output);
        words[32] = count; words[33] = stride / 4; words[34] = offset / 4; words[36] = (uint)program.WorldWarpWaving!.Value;
        var matrix = MemoryMarshal.Cast<byte, float>(output[..64]);
        foreach (float value in matrix) if (!float.IsFinite(value)) return false;
        return true;
    }

    private bool ReadGeometryWarpState(ShaderProgramResources program, bool previous, Span<byte> output)
    {
        output.Clear();
        string[] names = previous ? PreviousWorldWarpNames : WorldWarpNames;
        for (int i = 0; i < names.Length; i++)
        {
            if (!CopyWorldUniform(program, names[i], "float", output.Slice(i * 4, 4))) return false;
        }
        if (!CopyWorldUniform(program, previous ? "prevPlayerpos" : "playerpos", "vec3", output.Slice(48, 12)) ||
            !CopyWorldUniform(program, previous ? "prevPerceptionEffectId" : "perceptionEffectId", "int", output.Slice(60, 4))) return false;
        // Nonfinite state must keep the original shader path, never poison a retained buffer.
        var floats = MemoryMarshal.Cast<byte, float>(output[..60]);
        foreach (float value in floats) if (!float.IsFinite(value)) return false;
        return true;
    }

    private bool CopyWorldUniform(ShaderProgramResources program, string name, string type, Span<byte> target)
    {
        UniformMember? member;
        byte[]? shadow;
        if (program.Interface.FrameMemberDeclaredLengths.TryGetValue(name, out int length))
        {
            if (length != 0 || !FrameGlobals.TryGetMember(name, out member!)) return false;
            shadow = _frameGlobals;
        }
        else if (program.Interface.PushMembersByName.TryGetValue(name, out member)) shadow = program.PushShadow;
        else { program.Interface.MembersByName.TryGetValue(name, out member); shadow = program.UniformShadow; }
        if (member == null || member.Type.Name != type || member.ArrayLength != 0 || shadow == null ||
            member.Size < target.Length || member.Offset < 0 || member.Offset + target.Length > shadow.Length) return false;
        shadow.AsSpan(member.Offset, target.Length).CopyTo(target);
        return true;
    }

    private VulkanBuffer? WarpGeometry(VulkanBuffer local, VulkanBuffer flags, ReadOnlySpan<byte> parameters)
    {
        var hash = new HashCode(); hash.AddBytes(parameters);
        var key = (local.Id, hash.ToHashCode());
        if (worldGeometry.TryGetValue(key, out var versions))
            foreach (var geometry in versions)
                if (parameters.SequenceEqual(geometry.Parameters))
                { geometry.Frame = _frameCounter; WorldGeometryCacheReuses++; return geometry.Buffer; }
        ulong retained = local.Size + (ulong)parameters.Length;
        if (_deformationBytes + worldGeometryBytes + retained > DeformationBudget ||
            flags.Size > _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice).Limits.MaxStorageBufferRange ||
            !_frames.Current.TryAllocateUniforms(128, out var state)) return null;
        if (worldGeometryProgram == 0)
        {
            string source = ReadWorldShader("geometry/world.comp");
            foreach (string include in new[] { "vertexflagbits.glsl", "noise3d.glsl", "warpstate.glsl" })
                source = source.Replace("#include \"../include/" + include + "\"", ReadWorldShader("include/" + include), StringComparison.Ordinal);
            worldGeometryProgram = CreateComputeProgram(source, "world-deformation",
                [new ComputeSlot(0, ComputeSlotKind.Buffer), new ComputeSlot(1, ComputeSlotKind.Buffer),
                    new ComputeSlot(2, ComputeSlotKind.Buffer), new ComputeSlot(3, ComputeSlotKind.Buffer)], 20, 64, 1);
        }
        var program = _compute.Get(worldGeometryProgram);
        if (program == null) return null;
        parameters[..128].CopyTo(new Span<byte>((void*)state.Pointer, 128));
        var output = new VulkanBuffer(_context, local.Size, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.VertexBufferBit |
            BufferUsageFlags.TransferSrcBit, MemoryPropertyFlags.DeviceLocalBit, MemoryPoolClass.DeviceBuffers);
        try
        {
            var command = BeginProducerCommands(out bool hoisted, local, flags);
            using GpuSection section = hoisted ? default : BeginGpuSection("world-deformation");
            uint count = (uint)(local.Size / 64);
            if (!hoisted) _graph.OpenComputePass(new PassSignature { NameId = _graph.NameId("compute:world-deformation"),
                Width = (int)count, Height = 1, FormatsId = -1, Reads = [], Attachments = [] });
            var set = _computeArenas[CurrentFrameSlot].GetBuffers(program.SetLayout,
                [new DescriptorBufferInfo(local.Handle, 0, local.Size), new DescriptorBufferInfo(flags.Handle, 0, flags.Size),
                    new DescriptorBufferInfo(_frames.UniformBuffer, state.Offset, 128), new DescriptorBufferInfo(output.Handle, 0, output.Size)]);
            _context.Api.CmdBindPipeline(command, PipelineBindPoint.Compute, _compute.PipelineFor(program, []));
            _context.Api.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, program.Layout, 0, 1, &set, 0, null);
            fixed (byte* data = parameters)
                _context.Api.CmdPushConstants(command, program.Layout, ShaderStageFlags.ComputeBit, 0, 20, data + 128);
            _context.Api.CmdDispatch(command, (count + 63) / 64, 1, 1); _graph.NoteDispatch(); WorldGeometryDispatchesRecorded++;
            var barrier = new BufferMemoryBarrier2 { SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ComputeShaderBit, SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.CopyBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.TransferReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = output.Handle, Size = output.Size };
            var dependency = new DependencyInfo { SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 1, PBufferMemoryBarriers = &barrier };
            _context.Api.CmdPipelineBarrier2(command, &dependency);
            NoteProducerOutput(output, hoisted);
            if (versions == null) worldGeometry.Add(key, versions = new());
            versions.Add(new WorldGeometry(output, parameters.ToArray(), _frameCounter)); worldGeometryBytes += retained;
            return output;
        }
        catch { _frames.DeferDeletion(output); throw; }
    }

    private static string ReadWorldShader(string name)
    {
        using Stream stream = typeof(VulkanDevice).Assembly.GetManifestResourceStream("shaders-vk/" + name)!;
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }

    private void RetireWorldGeometry(bool all = false)
    {
        oldWorldGeometry.Clear();
        foreach (var pair in worldGeometry)
        {
            for (int i = pair.Value.Count - 1; i >= 0; i--)
                if (all || unchecked(_frameCounter - pair.Value[i].Frame) > 2)
                {
                    var geometry = pair.Value[i]; _descriptors.Release(geometry.Buffer.Id); _frames.DeferDeletion(geometry.Buffer);
                    worldGeometryBytes -= geometry.Buffer.Size + (ulong)geometry.Parameters.Length; pair.Value.RemoveAt(i);
                }
            if (pair.Value.Count == 0) oldWorldGeometry.Add(pair.Key);
        }
        foreach (var key in oldWorldGeometry) worldGeometry.Remove(key);
    }

    private void DisposeWorldGeometryIdle()
    {
        foreach (var versions in worldGeometry.Values) foreach (var geometry in versions) geometry.Buffer.Dispose();
        worldGeometry.Clear(); worldGeometryBytes = 0;
    }
}
