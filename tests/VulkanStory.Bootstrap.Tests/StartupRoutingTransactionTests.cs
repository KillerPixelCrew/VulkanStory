using VulkanStory.Game;
using Xunit;

namespace VulkanStory.Bootstrap.Tests;

/// <summary>Checks patch-group validation, commit, rollback order, and rollback-error reporting.</summary>
/// <remarks>Delegates simulate mutation and failure; no graphics device or game window is created.</remarks>
public sealed class StartupRoutingTransactionTests
{
    private static StartupPatchGroup[] Groups(List<string> calls, string? failValidation = null,
        string? failInstall = null, string? failRemove = null) =>
        StartupRoutingTransaction.RequiredGroups.Select(name => new StartupPatchGroup(name,
            () =>
            {
                calls.Add("validate:" + name);
                if (name == failValidation) throw new InvalidOperationException("binding failed");
            },
            () =>
            {
                calls.Add("install:" + name);
                if (name == failInstall) throw new InvalidOperationException("patch failed after mutation");
            },
            () =>
            {
                calls.Add("remove:" + name);
                if (name == failRemove) throw new InvalidOperationException("removal failed");
            })).ToArray();

    [Fact]
    public void MissingCoverageAndFailedBindingNeverInstallPatches()
    {
        var calls = new List<string>();
        using var incomplete = new StartupRoutingTransaction(Groups(calls).SkipLast(1));
        Assert.Throws<InvalidOperationException>(incomplete.Prepare);
        Assert.Empty(calls);
        Assert.False(incomplete.RoutingEnabled);

        using var badBinding = new StartupRoutingTransaction(Groups(calls, failValidation: "shutdown"));
        Assert.Throws<InvalidOperationException>(badBinding.Prepare);
        Assert.All(calls, call => Assert.StartsWith("validate:", call));
        Assert.False(badBinding.RoutingEnabled);
    }

    [Fact]
    public void PartiallyInstalledFailingGroupIsRemovedBeforeEarlierGroups()
    {
        var calls = new List<string>();
        using var transaction = new StartupRoutingTransaction(Groups(calls, failInstall: "graphics-api"));
        Assert.Throws<InvalidOperationException>(transaction.Prepare);
        Assert.Equal(new[]
        {
            "remove:graphics-api", "remove:window-consumers", "remove:window-creation", "remove:platform-construction",
        }, calls.Where(call => call.StartsWith("remove:", StringComparison.Ordinal)));
        Assert.False(transaction.RoutingEnabled);
        Assert.Equal(StartupRoutingState.Faulted, transaction.State);
    }

    [Fact]
    public void RoutingCommitsOnlyAfterSessionPreparationAndDisablesBeforeRemoval()
    {
        var calls = new List<string>();
        StartupRoutingTransaction? transaction = null;
        StartupPatchGroup[] groups = Groups(calls).Select(group => group with
        {
            Remove = () =>
            {
                Assert.False(transaction!.RoutingEnabled);
                group.Remove();
            },
        }).ToArray();
        transaction = new StartupRoutingTransaction(groups);
        transaction.Prepare();
        Assert.False(transaction.RoutingEnabled);
        transaction.Commit(() => Assert.False(transaction.RoutingEnabled));
        Assert.True(transaction.RoutingEnabled);
        transaction.Dispose();
        Assert.False(transaction.RoutingEnabled);
        Assert.Equal(StartupRoutingTransaction.RequiredGroups.Reverse().Select(name => "remove:" + name),
            calls.Where(call => call.StartsWith("remove:", StringComparison.Ordinal)));
    }

    [Fact]
    public void SessionPreparationFailureRemovesAllPatchesWithoutActivatingRouting()
    {
        var calls = new List<string>();
        using var transaction = new StartupRoutingTransaction(Groups(calls));
        transaction.Prepare();
        Assert.Throws<InvalidOperationException>(() => transaction.Commit(() => throw new InvalidOperationException("device failed")));
        Assert.Equal(StartupRoutingTransaction.RequiredGroups.Count,
            calls.Count(call => call.StartsWith("remove:", StringComparison.Ordinal)));
        Assert.False(transaction.RoutingEnabled);
    }

    [Fact]
    public void RollbackFailureIsReportedAndDoesNotPreventOtherRemovals()
    {
        var calls = new List<string>();
        using var transaction = new StartupRoutingTransaction(Groups(calls,
            failInstall: "graphics-api", failRemove: "window-consumers"));
        AggregateException failure = Assert.Throws<AggregateException>(transaction.Prepare);
        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.Contains("remove:platform-construction", calls);
        Assert.False(transaction.RoutingEnabled);
    }
}
