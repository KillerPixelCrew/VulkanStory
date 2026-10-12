using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    internal readonly record struct RigidEntityScope(object? Identity, long CapturedAfter);
    private RigidEntityScope rigidEntityScope;
    internal long RigidEntityMotionWindows { get; private set; }
    internal long ValidRigidEntityHistoryWrites { get; private set; }
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
        if (FrameState.MotionWriteActive || rigidEntityScope.Identity == null ||
            aoTemporal?.EntityMotion.Enabled != true || !aoTemporal.State.JitterActive ||
            ShaderProgramBase.CurrentShaderProgram is not { } program || !HasRigidMotionContract(program) ||
            !aoTemporal.EntityMotion.TryGetCurrentModel(program.ProgramId, rigidEntityScope.CapturedAfter, out var model)) return false;
        if (!BeginMotionWrite(aoTemporal)) return false;
        try
        {
            if (aoTemporal.StandardMotion.Apply(program, rigidEntityScope.Identity, shape, model)) ValidRigidEntityHistoryWrites++;
            RigidEntityMotionWindows++;
            return true;
        }
        catch { EndMotionWrite(); throw; }
    }
}
