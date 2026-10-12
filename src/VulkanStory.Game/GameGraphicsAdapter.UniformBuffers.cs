using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using HarmonyLib;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    private sealed record BufferOwner(GameGraphicsAdapter Adapter, int Handle, int Binding, string Block);
    private sealed record PreviousAnimation(UBO Buffer, Action<float[], int> Upload);
    private readonly Dictionary<int, PreviousAnimation> previousAnimations = new();
    private static readonly ConditionalWeakTable<UBO, BufferOwner> UniformBuffers = new();
    private static readonly Action<UBORef, bool> SetUniformBufferDisposed =
        AccessTools.PropertySetter(typeof(UBORef), nameof(UBORef.Disposed)).CreateDelegate<Action<UBORef, bool>>();

    /// <summary>Creates a backend named-block buffer and attaches its ownership/binding metadata to the original UBO reference.</summary>
    /// <param name="program">Backend program identifier owning the named block.</param>
    /// <param name="binding">Original uniform block binding index.</param>
    /// <param name="block">Original block name.</param>
    /// <param name="size">Requested block capacity in bytes.</param>
    /// <returns>Original UBO reference carrying adapter ownership.</returns>
    internal UBORef CreateUniformBuffer(int program, int binding, string block, int size)
    {
        int handle = RequireDevice().CreateUniformBuffer(program, binding, block, size);
        var buffer = new UBO { Handle = handle, Size = size };
        // Block/binding metadata belonged to injected members in the old project.
        UniformBuffers.Add(buffer, new BufferOwner(this, handle, binding, block));
        return buffer;
    }

    internal static GameGraphicsAdapter UniformBufferOwner(UBO buffer) =>
        UniformBuffers.TryGetValue(buffer, out var owner) ? owner.Adapter :
            throw new InvalidOperationException("Active UBO routing has no renderer owner.");

    private int UniformBufferHandle(UBO buffer)
    {
        if (!UniformBuffers.TryGetValue(buffer, out var owner) || !ReferenceEquals(owner.Adapter, this))
            throw new InvalidOperationException("Uniform buffer belongs to another renderer owner.");
        return owner.Handle;
    }

    internal void BindUniformBuffer(UBO buffer) => RequireDevice().BindUniformBuffer(UniformBufferHandle(buffer));
    internal void UnbindUniformBuffer(UBO buffer) => RequireDevice().UnbindUniformBuffer(UniformBufferHandle(buffer));
    internal void DeleteUniformBuffer(UBO buffer)
    {
        int handle = UniformBufferHandle(buffer);
        aoTemporal?.EntityMotion.InvalidateBuffer(handle);
        RequireDevice().DeleteUniformBuffer(handle);
    }
    internal void UploadUniformBuffer(UBO buffer, IntPtr data, int offset, int size)
    {
        InvalidateAnimationPayload(buffer);
        RequireDevice().UpdateUniformBuffer(UniformBufferHandle(buffer), data, offset, size);
    }
    internal byte[]? UniformBufferShadowForTests(UBO buffer) =>
        RequireDevice().UniformBufferShadowForTests(UniformBufferHandle(buffer));
    internal void UpdateUniformBuffer(UBO buffer, object data, int offset, int size)
    {
        var renderer = RequireDevice();
        int handle = UniformBufferHandle(buffer);
        InvalidateAnimationPayload(buffer);
        renderer.BindUniformBuffer(handle);
        GCHandle pin = default;
        try
        {
            pin = GCHandle.Alloc(data, GCHandleType.Pinned);
            renderer.UpdateUniformBuffer(handle, pin.AddrOfPinnedObject(), offset, size);
        }
        finally { if (pin.IsAllocated) pin.Free(); renderer.UnbindUniformBuffer(handle); }
        ObserveAnimationPayload(buffer, data, offset, size);
    }
    private void InvalidateAnimationPayload(UBO buffer)
    {
        if (UniformBuffers.TryGetValue(buffer, out var owner)) aoTemporal?.EntityMotion.InvalidateBuffer(owner.Handle);
    }
    internal void ObserveAnimationPayload(UBO buffer, object data, int offset, int size)
    {
        var renderer = RequireDevice();
        if (offset != 0 || size <= 0 || size % 64 != 0 || size > buffer.Size ||
            data is not float[] bones || size / 4 > bones.Length || aoTemporal?.EntityMotion.Enabled != true ||
            !UniformBuffers.TryGetValue(buffer, out var owner) || !ReferenceEquals(owner.Adapter, this) || owner.Block != "Animation") return;
        ShaderProgramBase? program = ShaderProgramBase.CurrentShaderProgram;
        if (program == null || StatedProgram != program.ProgramId ||
            !program.ubos.TryGetValue("Animation", out var current) || !ReferenceEquals(current, buffer) ||
            renderer.ClientUniformBufferBoundHandle("Animation") != owner.Handle ||
            !renderer.TryGetAnimatedMotionContract(program.ProgramId, FrameState.MotionAttachment, out int capacity) ||
            (capacity > 0 && size / 64 > capacity) || !aoTemporal.EntityMotion.HasCurrentModel(program.ProgramId)) return;
        int? binding = renderer.ClientUniformBlockBinding(program.ProgramId, "AnimationPrev");
        if (!binding.HasValue) { RejectSceneDraw("previous animation block binding is absent"); return; }
        if (!previousAnimations.TryGetValue(program.ProgramId, out var previous) || previous.Buffer.Disposed)
        {
            UBO previousBuffer;
            if (program.ubos.TryGetValue("AnimationPrev", out var declared) && !declared.Disposed)
            {
                previousBuffer = declared as UBO ?? throw new InvalidOperationException("Previous animation block has a foreign buffer type.");
                _ = UniformBufferHandle(previousBuffer);
            }
            else
            {
                previousBuffer = (UBO)CreateUniformBuffer(program.ProgramId, binding.Value, "AnimationPrev", buffer.Size);
                program.ubos["AnimationPrev"] = previousBuffer;
            }
            previous = new PreviousAnimation(previousBuffer,
                (bones, bytes) => UpdateUniformBuffer(previousBuffer, bones, 0, bytes));
            previousAnimations[program.ProgramId] = previous;
        }
        if (previous.Buffer.Size < size) return;
        aoTemporal.EntityMotion.OnAnimationUpload(program, owner.Handle, UniformBufferHandle(previous.Buffer), previous.Upload, data, size);
    }
    internal void ReleasePreviousAnimation(int program)
    {
        RequireDevice();
        if (previousAnimations.Remove(program, out var previous) && !previous.Buffer.Disposed) previous.Buffer.Dispose();
    }
    internal void ReleasePreviousAnimations()
    {
        var renderer = RequireLifecycleDevice();
        var failures = new List<Exception>();
        foreach (int program in previousAnimations.Keys.ToArray())
        {
            try
            {
                var previous = previousAnimations[program];
                if (!previous.Buffer.Disposed)
                {
                    // UBO.Dispose may route through patches, or fall back to GL
                    // after rollback. Destroy our native resource directly.
                    renderer.DeleteUniformBuffer(UniformBufferHandle(previous.Buffer));
                    SetUniformBufferDisposed(previous.Buffer, true);
                }
                previousAnimations.Remove(program);
            }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count != 0) throw new AggregateException("Previous animation cleanup failed.", failures);
        aoTemporal?.EntityMotion.Clear();
        motionInputLocations.Clear();
    }
}
