namespace Optimum.Render.Vulkan.Present;

/// <summary>Which half of an output pair a present carried.</summary>
internal enum PacedPresentKind
{
    /// <summary>The interpolated frame, presented as soon as its pair arrives.</summary>
    Generated,

    /// <summary>The retained real frame, presented when the pacer says.</summary>
    Real,
}

/// <summary>
/// The contract between the present thread and the pacer (ROADMAP, "The paced
/// present: the design"). The present thread reports every present and asks when
/// the real frame of a pair is due; the pacer owns the spacing. Both sides
/// implement exactly this; the wiring stage connects them.
/// </summary>
internal interface IFramePacer
{
    /// <summary>The present thread reports each vkQueuePresentKHR return, in microseconds from LatencyClock.NowUs().</summary>
    void NotePresented(PacedPresentKind kind, long presentReturnUs);

    /// <summary>
    /// After the generated present of a pair returned at <paramref name="generatedReturnUs" />:
    /// when to present that pair's real frame. A value &lt;= now means present immediately.
    /// <paramref name="nextPairQueued" /> = another pair is already waiting.
    /// </summary>
    long RealPresentTargetUs(long generatedReturnUs, bool nextPairQueued);
}

/// <summary>
/// The stand-in pacer until the pacer stream's real one is wired: every real frame
/// is presented immediately after its generated frame. It exists so the present
/// thread can be built and tested on its own; with it, spacing is whatever the
/// present mode makes of two back-to-back presents, which is exactly the unpaced
/// behaviour the user requirement forbids shipping ("DLSSFG without pacing
/// (Reflex) is useless and unplayable") - so nothing but tests and the
/// OPTIMUM_DLSSG_DOUBLE_PRESENT switch may run with it.
/// </summary>
internal sealed class ImmediateFramePacer : IFramePacer
{
    public void NotePresented(PacedPresentKind kind, long presentReturnUs)
    {
    }

    public long RealPresentTargetUs(long generatedReturnUs, bool nextPairQueued) => long.MinValue;
}
