namespace Optimum.Render.Vulkan.Present;

/// <summary>
/// Which half of a frame-generation pair a present carried. The paced present
/// (ROADMAP, "The paced present: the design") issues the generated frame as soon
/// as its pair arrives and the retained real frame half a rendered interval later.
/// </summary>
internal enum PacedPresentKind
{
    Generated,
    Real,
}

/// <summary>
/// The contract between the present thread and the pacer. Both sides implement
/// exactly this; the wiring stage connects them.
/// </summary>
internal interface IFramePacer
{
    /// <summary>
    /// The present thread reports each vkQueuePresentKHR return, in microseconds
    /// from <c>LatencyClock.NowUs()</c>.
    /// </summary>
    void NotePresented(PacedPresentKind kind, long presentReturnUs);

    /// <summary>
    /// After the generated present of a pair returned at <paramref name="generatedReturnUs" />:
    /// when to present that pair's real frame. A value &lt;= now means present
    /// immediately. <paramref name="nextPairQueued" /> = another pair is already waiting.
    /// </summary>
    long RealPresentTargetUs(long generatedReturnUs, bool nextPairQueued);
}
