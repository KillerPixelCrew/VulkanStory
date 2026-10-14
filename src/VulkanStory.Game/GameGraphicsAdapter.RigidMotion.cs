using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    internal readonly record struct RigidEntityScope(object? Identity, long CapturedAfter);
    private RigidEntityScope rigidEntityScope;
    internal long RigidEntityMotionWindows { get; private set; }
    internal long ValidRigidEntityHistoryWrites { get; private set; }
    internal long InventoryMotionWindows { get; private set; }
    internal long ValidInventoryHistoryWrites { get; private set; }
    internal long InventorySharedGeometryDraws { get; private set; }
    internal void NoteInventorySharedGeometryDraw() => InventorySharedGeometryDraws++;
    internal bool HasRigidEntityMotionScope => rigidEntityScope.Identity != null;

    // The caller is the actual entity dispatcher, never a mesh or draw ordinal.
    internal RigidEntityScope EnterRigidEntityMotionScope(object? identity)
    {
        var previous = rigidEntityScope;
        rigidEntityScope = new(identity, aoTemporal?.EntityMotion.ModelCaptureSerial ?? 0);
        return previous;
    }
    internal void ExitRigidEntityMotionScope(RigidEntityScope previous)
    {
        // Nested dispatch can overwrite GPU model state. The resumed producer must upload again.
        rigidEntityScope = previous with { CapturedAfter = aoTemporal?.EntityMotion.ModelCaptureSerial ?? 0 };
    }
    internal bool HasRigidMotionContract(ShaderProgramBase program) => StatedProgram == program.ProgramId &&
        RequireDevice().HasRigidMotionContract(program.ProgramId, FrameState.MotionAttachment);

    internal bool BeginRigidEntityMotionWrite(object shape)
    {
        if (rigidEntityScope.Identity == null ||
            !BeginRigidObjectMotionWrite(rigidEntityScope.Identity, shape, rigidEntityScope.CapturedAfter, out bool valid)) return false;
        if (valid) ValidRigidEntityHistoryWrites++;
        RigidEntityMotionWindows++;
        return true;
    }
    internal bool BeginInventoryMotionWrite(object identity, object shape, long capturedAfter)
    {
        if (!BeginRigidObjectMotionWrite(identity, shape, capturedAfter, out bool valid)) return false;
        if (valid) ValidInventoryHistoryWrites++;
        InventoryMotionWindows++;
        return true;
    }
    private bool BeginRigidObjectMotionWrite(object identity, object shape, long capturedAfter, out bool valid)
    {
        valid = false;
        if (FrameState.MotionWriteActive ||
            aoTemporal?.EntityMotion.Enabled != true || !aoTemporal.State.JitterActive ||
            ShaderProgramBase.CurrentShaderProgram is not { } program || !HasRigidMotionContract(program) ||
            !aoTemporal.EntityMotion.TryGetCurrentModel(program.ProgramId, capturedAfter, out var model)) return false;
        if (!BeginMotionWrite(aoTemporal)) return false;
        try
        {
            valid = aoTemporal.StandardMotion.Apply(program, identity, shape, model);
            return true;
        }
        catch { EndMotionWrite(); throw; }
    }
}
