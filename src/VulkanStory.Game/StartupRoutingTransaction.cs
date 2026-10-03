namespace VulkanStory.Game;

internal sealed record StartupPatchGroup(string Name, Action Validate, Action Install, Action Remove);

internal enum StartupRoutingState
{
    Fresh,
    Validating,
    Installing,
    Prepared,
    PreparingSession,
    Active,
    Removed,
    Faulted,
}

/// <summary>
/// Startup-only patch transaction. Prefixes read RoutingEnabled; it stays false
/// until every mandatory group is installed and the window/render session is ready.
/// </summary>
internal sealed class StartupRoutingTransaction : IDisposable
{
    internal static readonly IReadOnlyList<string> RequiredGroups =
    [
        "platform-construction", "window-creation", "window-consumers",
        "graphics-api", "frame-loop", "shutdown",
    ];

    private readonly StartupPatchGroup[] groups;
    private readonly List<StartupPatchGroup> attempted = new();
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private int state;

    public StartupRoutingTransaction(IEnumerable<StartupPatchGroup> groups) => this.groups = groups.ToArray();

    public StartupRoutingState State => (StartupRoutingState)Volatile.Read(ref state);
    public bool RoutingEnabled => State == StartupRoutingState.Active;

    public void Prepare()
    {
        RequireOwner();
        RequireState(StartupRoutingState.Fresh);
        try
        {
            SetState(StartupRoutingState.Validating);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (StartupPatchGroup group in groups)
            {
                if (!names.Add(group.Name)) throw new InvalidOperationException("Duplicate startup patch group: " + group.Name);
                if (!RequiredGroups.Contains(group.Name, StringComparer.Ordinal))
                    throw new InvalidOperationException("Unknown startup patch group: " + group.Name);
            }
            foreach (string required in RequiredGroups)
                if (!names.Contains(required)) throw new InvalidOperationException("Missing startup patch group: " + required);

            // All binding and instruction checks complete before the first mutation.
            foreach (StartupPatchGroup group in groups) group.Validate();
            SetState(StartupRoutingState.Installing);
            foreach (StartupPatchGroup group in groups)
            {
                // An install may mutate a method and then throw. Its removal must run too.
                attempted.Add(group);
                group.Install();
            }
            SetState(StartupRoutingState.Prepared);
        }
        catch (Exception failure)
        {
            SetState(StartupRoutingState.Faulted);
            ThrowIfRemovalFailed(failure, RemoveAttempted());
            throw;
        }
    }

    /// <summary>
    /// Prepares SDL/Vulkan while patches are dormant, then commits routing. The
    /// session factory must release its partial resources before propagating failure.
    /// </summary>
    public void Commit(Action prepareSession)
    {
        RequireOwner();
        RequireState(StartupRoutingState.Prepared);
        ArgumentNullException.ThrowIfNull(prepareSession);
        try
        {
            SetState(StartupRoutingState.PreparingSession);
            prepareSession();
            SetState(StartupRoutingState.Active);
        }
        catch (Exception failure)
        {
            SetState(StartupRoutingState.Faulted);
            ThrowIfRemovalFailed(failure, RemoveAttempted());
            throw;
        }
    }

    /// <summary>The owner stops and drains its graphics session before removing active patches.</summary>
    public void Dispose()
    {
        RequireOwner();
        StartupRoutingState current = State;
        if (current is StartupRoutingState.Removed or StartupRoutingState.Faulted) return;
        if (current is StartupRoutingState.Validating or StartupRoutingState.Installing or StartupRoutingState.PreparingSession)
            throw new InvalidOperationException("Cannot remove a startup transaction from inside an unfinished operation.");
        SetState(StartupRoutingState.Removed);
        List<Exception> failures = RemoveAttempted();
        if (failures.Count == 0) return;
        SetState(StartupRoutingState.Faulted);
        throw new AggregateException("Startup patch removal failed; routing remains disabled.", failures);
    }

    private List<Exception> RemoveAttempted()
    {
        var failures = new List<Exception>();
        for (int index = attempted.Count - 1; index >= 0; index--)
        {
            try { attempted[index].Remove(); }
            catch (Exception failure) { failures.Add(new InvalidOperationException("Removing " + attempted[index].Name + " failed.", failure)); }
        }
        attempted.Clear();
        return failures;
    }

    private static void ThrowIfRemovalFailed(Exception original, List<Exception> removalFailures)
    {
        if (removalFailures.Count == 0) return;
        removalFailures.Insert(0, original);
        throw new AggregateException("Startup preparation and rollback failed; routing remains disabled.", removalFailures);
    }

    private void RequireOwner()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("Startup patch mutation must run on its owning thread.");
    }

    private void RequireState(StartupRoutingState required)
    {
        if (State != required) throw new InvalidOperationException($"Startup transaction is {State}; expected {required}.");
    }

    private void SetState(StartupRoutingState value) => Volatile.Write(ref state, (int)value);
}
