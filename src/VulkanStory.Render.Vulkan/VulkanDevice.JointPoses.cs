using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Graph;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    private sealed class JointPoseBuffer(VulkanBuffer buffer, uint frame)
    {
        internal readonly VulkanBuffer Buffer = buffer;
        internal uint Frame = frame;
    }
    private readonly Dictionary<JointPoseSnapshot, JointPoseBuffer> jointPoseBuffers = new();
    private readonly List<JointPoseSnapshot> oldJointPoses = new();
    private ulong jointPoseBytes;
    private int jointPoseProgram;
    internal bool ComputeJointPosesEnabled { get; set; }
    internal long JointPoseDispatchesRecorded { get; private set; }
    internal long JointPoseCacheReuses { get; private set; }
    private VulkanBuffer?[] jointChecks = Array.Empty<VulkanBuffer?>();
    private JointPoseSnapshot?[] pendingJointChecks = Array.Empty<JointPoseSnapshot?>();
    private float[] jointCheckScratch = [];
    internal long JointPoseChecks { get; private set; }
    internal double JointPoseMaximumRelativeError { get; private set; }
    internal bool JointPoseChecksPassed => JointPoseChecks > 0 && JointPoseMaximumRelativeError <= 0.00001;
    private static readonly bool CheckJointPoses = Environment.GetEnvironmentVariable("VULKANSTORY_HEADLESS") == "1";

    internal bool SetJointPose(int handle, JointPoseSnapshot pose) =>
        _clientUniformBuffers.Buffers.TryGetValue(handle, out var buffer) && buffer.SetJointPose(pose);
    internal bool SupportsGpuJointPoses(int program) => _programs.TryGetValue(program, out var shader) &&
        shader.LocationOf("vsComputeDeformation") >= 0 && shader.Interface.StorageBlocks.Exists(block => block.BlockName == DeformationBlock);

    private VulkanBuffer? drawCurrentJointPose, drawPreviousJointPose;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PrepareProgramJointPoses(ShaderProgramResources program)
    {
        drawCurrentJointPose = drawPreviousJointPose = null;
        foreach (var block in program.Interface.UniformBlocks)
        {
            if (block.BlockName is not ("Animation" or "AnimationPrev") ||
                !_clientUniformBuffers.Buffers.TryGetValue(_clientUniformBuffers.BoundHandle(block.BlockName), out var buffer)) continue;
            var storage = PrepareJointPose(buffer);
            if (storage == null) buffer.MaterializeJointPose();
            if (block.BlockName == "Animation") drawCurrentJointPose = storage;
            else drawPreviousJointPose = storage;
        }
    }

    private VulkanBuffer? PrepareJointPose(ClientUniformBuffer source)
    {
        if (!ComputeJointPosesEnabled || source.JointPose is not { } pose) return null;
        if (jointPoseBuffers.TryGetValue(pose, out var cached))
        {
            cached.Frame = _frameCounter; JointPoseCacheReuses++;
            return cached.Buffer;
        }
        ulong bytes = (ulong)pose.JointCount * 64;
        if (jointPoseBytes + bytes + (ulong)pose.Bytes.Length > 8UL << 20 ||
            !_frames.Current.TryAllocateUniforms(pose.Bytes.Length, out var input)) return null;
        if (jointPoseProgram == 0)
        {
            using Stream stream = typeof(VulkanDevice).Assembly.GetManifestResourceStream("shaders-vk/geometry/joints.comp")!;
            using var reader = new StreamReader(stream);
            jointPoseProgram = CreateComputeProgram(reader.ReadToEnd(), "joint-poses",
                [new ComputeSlot(0, ComputeSlotKind.Buffer), new ComputeSlot(1, ComputeSlotKind.Buffer)], 4, 64, 1);
        }
        var program = _compute.Get(jointPoseProgram);
        if (program == null) return null;
        pose.Bytes.CopyTo(new Span<byte>((void*)input.Pointer, pose.Bytes.Length));
        var output = RentComputeOutput(bytes, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.UniformBufferBit | BufferUsageFlags.TransferSrcBit);
        try
        {
            // Reads only host-written ring bytes, which no frame command writes.
            var command = BeginProducerCommands(out bool hoisted, null);
            using GpuSection section = hoisted ? default : BeginGpuSection("joint-poses");
            if (!hoisted) _graph.OpenComputePass(new PassSignature
            { NameId = _graph.NameId("compute:joint-poses"), Width = pose.JointCount, Height = 1, FormatsId = -1, Reads = [], Attachments = [] });
            var set = _computeArenas[CurrentFrameSlot].GetBuffers(program.SetLayout,
                [new DescriptorBufferInfo(_frames.UniformBuffer, input.Offset, (ulong)pose.Bytes.Length),
                    new DescriptorBufferInfo(output.Handle, 0, bytes)]);
            _context.Api.CmdBindPipeline(command, PipelineBindPoint.Compute, _compute.PipelineFor(program, []));
            _context.Api.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, program.Layout, 0, 1, &set, 0, null);
            uint count = (uint)pose.JointCount;
            _context.Api.CmdPushConstants(command, program.Layout, ShaderStageFlags.ComputeBit, 0, 4, &count);
            _context.Api.CmdDispatch(command, (count + 63) / 64, 1, 1); _graph.NoteDispatch(); JointPoseDispatchesRecorded++;
            var barrier = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2, SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
                SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = PipelineStageFlags2.AllGraphicsBit | PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.CopyBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.UniformReadBit | AccessFlags2.TransferReadBit, SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored, Buffer = output.Handle, Size = bytes,
            };
            var dependency = new DependencyInfo { SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 1, PBufferMemoryBarriers = &barrier };
            _context.Api.CmdPipelineBarrier2(command, &dependency);
            CaptureJointCheck(output, pose, command);
            NoteProducerOutput(output, hoisted);
            jointPoseBuffers.Add(pose.Retain(), new JointPoseBuffer(output, _frameCounter));
            jointPoseBytes += bytes + (ulong)pose.Bytes.Length;
            return output;
        }
        catch { RetireComputeOutput(output); throw; }
    }

    private void RetireJointPoses(bool all = false)
    {
        oldJointPoses.Clear();
        foreach (var pair in jointPoseBuffers)
            if (all || unchecked(_frameCounter - pair.Value.Frame) > 2)
            {
                _descriptors.Release(pair.Value.Buffer.Id);
                RetireComputeOutput(pair.Value.Buffer);
                jointPoseBytes -= pair.Value.Buffer.Size + (ulong)pair.Key.Bytes.Length;
                oldJointPoses.Add(pair.Key);
            }
        foreach (var pose in oldJointPoses) { jointPoseBuffers.Remove(pose); pose.Release(); }
        oldJointPoses.Clear();
    }

    private void DisposeJointPosesIdle()
    {
        foreach (var buffer in _clientUniformBuffers.Buffers.Values) buffer.ClearJointPose();
        foreach (var pair in jointPoseBuffers) { pair.Value.Buffer.Dispose(); pair.Key.Release(); }
        jointPoseBuffers.Clear(); jointPoseBytes = 0;
        foreach (var check in jointChecks) check?.Dispose();
        foreach (var pose in pendingJointChecks) pose?.Release();
        jointChecks = Array.Empty<VulkanBuffer?>(); pendingJointChecks = Array.Empty<JointPoseSnapshot?>();
    }

    private void CaptureJointCheck(VulkanBuffer output, JointPoseSnapshot pose, CommandBuffer command)
    {
        if (!CheckJointPoses) return;
        if (jointChecks.Length == 0)
        {
            jointChecks = new VulkanBuffer?[_frames.FramesInFlight];
            pendingJointChecks = new JointPoseSnapshot?[_frames.FramesInFlight];
        }
        int slot = CurrentFrameSlot;
        if (pendingJointChecks[slot] != null) return;
        if (jointChecks[slot] is not { } readback || readback.Size < output.Size)
        {
            if (jointChecks[slot] is { } old) _frames.DeferDeletion(old);
            readback = jointChecks[slot] = CreateReadbackBuffer(output.Size);
        }
        var copy = new BufferCopy { Size = output.Size };
        _context.Api.CmdCopyBuffer(command, output.Handle, readback.Handle, 1, &copy);
        ReadbackManager.RecordHostReadBarrier(_context, command, readback, 0, output.Size);
        pendingJointChecks[slot] = pose.Retain();
    }

    private void CollectJointCheck(int slot)
    {
        if (pendingJointChecks.Length <= slot || pendingJointChecks[slot] is not { } pose || jointChecks[slot] is not { } buffer) return;
        pendingJointChecks[slot] = null;
        int length = pose.JointCount * 16;
        if (jointCheckScratch.Length < length) Array.Resize(ref jointCheckScratch, length);
        Span<float> expected = jointCheckScratch.AsSpan(0, length); pose.CopyMatrices(expected);
        var actual = new ReadOnlySpan<float>((void*)buffer.Mapped, expected.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            double error = Math.Abs((double)expected[i] - actual[i]) / Math.Max(1, Math.Abs((double)expected[i]));
            JointPoseMaximumRelativeError = Math.Max(JointPoseMaximumRelativeError, double.IsFinite(error) ? error : double.MaxValue);
        }
        JointPoseChecks++;
        pose.Release();
    }
}
