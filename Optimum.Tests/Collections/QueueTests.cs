// Source: Optimum.Tests/SortableQueueItemAtTests.cs
namespace Optimum.Tests
{
using Vintagestory.API.Datastructures;
using Xunit;

public class SortableQueueItemAtTests
{
    [Fact(Skip = "Requires fork patches: ItemAt")]
    public void ItemAtMatchesQueueOrderBeforeAnyWraparound()
    {
        var q = new SortableQueue<ComparableInt>();
        q.Enqueue(new ComparableInt(1));
        q.Enqueue(new ComparableInt(2));
        q.Enqueue(new ComparableInt(3));

        Assert.Equal(1, q.ItemAt(0).Value);
        Assert.Equal(2, q.ItemAt(1).Value);
        Assert.Equal(3, q.ItemAt(2).Value);
    }

    [Fact(Skip = "Requires fork patches: ItemAt")]
    public void ItemAtMatchesQueueOrderAfterWraparound()
    {
        // maxSize starts at 27; dequeue past the front repeatedly to push
        // head forward until head % maxSize > tail % maxSize (wrapped).
        var q = new SortableQueue<ComparableInt>();
        for (int i = 0; i < 27; i++) q.Enqueue(new ComparableInt(i));
        for (int i = 0; i < 25; i++) q.Dequeue();
        // head is now 25, tail is 27. Enqueue more so tail wraps past maxSize.
        q.Enqueue(new ComparableInt(100));
        q.Enqueue(new ComparableInt(101));
        q.Enqueue(new ComparableInt(102));
        q.Enqueue(new ComparableInt(103));

        // Logical order should still be: 25, 26, 100, 101, 102, 103
        int[] expected = { 25, 26, 100, 101, 102, 103 };
        Assert.Equal(expected.Length, q.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], q.ItemAt(i).Value);
        }
    }

    [Fact(Skip = "Requires fork patches: ItemAt")]
    public void ItemAtMatchesRunForEachOrder()
    {
        var q = new SortableQueue<ComparableInt>();
        for (int i = 0; i < 10; i++) q.Enqueue(new ComparableInt(i));
        for (int i = 0; i < 5; i++) q.Dequeue();
        for (int i = 10; i < 15; i++) q.Enqueue(new ComparableInt(i));

        var viaRunForEach = new System.Collections.Generic.List<int>();
        q.RunForEach(item => viaRunForEach.Add(item.Value));

        var viaItemAt = new System.Collections.Generic.List<int>();
        for (int i = 0; i < q.Count; i++) viaItemAt.Add(q.ItemAt(i).Value);

        Assert.Equal(viaRunForEach, viaItemAt);
    }

    private sealed class ComparableInt(int value) : System.IComparable<ComparableInt>
    {
        public int Value { get; } = value;
        public int CompareTo(ComparableInt other) => Value.CompareTo(other.Value);
    }
}
}

// Source: Optimum.Tests/UniqueQueueTests.cs
namespace Optimum.Tests
{
using System.Linq;
using Vintagestory.API.Datastructures;
using Xunit;

public class UniqueQueueTests
{
    [Fact]
    public void EnqueueThenDequeuePreservesFifoOrder()
    {
        var q = new UniqueQueue<int>();
        q.Enqueue(1);
        q.Enqueue(2);
        q.Enqueue(3);

        Assert.Equal(1, q.Dequeue());
        Assert.Equal(2, q.Dequeue());
        Assert.Equal(3, q.Dequeue());
    }

    [Fact]
    public void EnqueueDuplicateIsIgnored()
    {
        var q = new UniqueQueue<int>();
        q.Enqueue(1);
        q.Enqueue(1);
        q.Enqueue(1);

        Assert.Equal(1, q.Count);
        Assert.Equal(1, q.Dequeue());
    }

    [Fact]
    public void CountAndContainsReflectDedupedState()
    {
        var q = new UniqueQueue<string>();
        q.Enqueue("a");
        q.Enqueue("b");
        q.Enqueue("a");

        Assert.Equal(2, q.Count);
        Assert.True(q.Contains("a"));
        Assert.True(q.Contains("b"));
        Assert.False(q.Contains("c"));
    }

    [Fact]
    public void PeekDoesNotRemove()
    {
        var q = new UniqueQueue<int>();
        q.Enqueue(42);

        Assert.Equal(42, q.Peek());
        Assert.Equal(1, q.Count);
        Assert.Equal(42, q.Dequeue());
    }

    [Fact]
    public void RemoveMiddleItemPreservesOrderOfSurvivors()
    {
        var q = new UniqueQueue<int>();
        q.Enqueue(1);
        q.Enqueue(2);
        q.Enqueue(3);
        q.Enqueue(4);

        q.Remove(2);

        Assert.Equal(3, q.Count);
        Assert.False(q.Contains(2));
        Assert.Equal(new[] { 1, 3, 4 }, q.ToArray());
    }

    [Fact]
    public void RemoveFirstItemLeavesRemainingOrderIntact()
    {
        var q = new UniqueQueue<int>();
        q.Enqueue(1);
        q.Enqueue(2);
        q.Enqueue(3);

        q.Remove(1);

        Assert.Equal(new[] { 2, 3 }, q.ToArray());
        Assert.Equal(2, q.Dequeue());
    }

    [Fact]
    public void RemoveLastItemLeavesRemainingOrderIntact()
    {
        var q = new UniqueQueue<int>();
        q.Enqueue(1);
        q.Enqueue(2);
        q.Enqueue(3);

        q.Remove(3);

        Assert.Equal(new[] { 1, 2 }, q.ToArray());
    }

    [Fact]
    public void RemoveItemNotPresentIsANoOp()
    {
        var q = new UniqueQueue<int>();
        q.Enqueue(1);
        q.Enqueue(2);

        q.Remove(99);

        Assert.Equal(2, q.Count);
        Assert.Equal(new[] { 1, 2 }, q.ToArray());
    }

    [Fact]
    public void RemoveThenReenqueueSameValueBehavesAsAFreshEntry()
    {
        // The exact case a naive tombstone-set design gets wrong: removing a
        // value, then enqueuing that same value again before the old queue
        // position is physically gone, must not have the stale removal
        // confuse the new entry's fate.
        var q = new UniqueQueue<int>();
        q.Enqueue(1);
        q.Enqueue(2);
        q.Enqueue(3);

        q.Remove(2);
        q.Enqueue(2);

        Assert.Equal(3, q.Count);
        Assert.True(q.Contains(2));
        // 2 re-enters at the back, after the removal, not at its old slot.
        Assert.Equal(new[] { 1, 3, 2 }, q.ToArray());
    }

    [Fact]
    public void RemoveDrainsToEmptyCorrectly()
    {
        var q = new UniqueQueue<int>();
        q.Enqueue(1);
        q.Remove(1);

        Assert.Equal(0, q.Count);
        Assert.False(q.Contains(1));
        Assert.Empty(q.ToArray());
    }

    [Fact]
    public void ClearEmptiesBothTrackingStructures()
    {
        var q = new UniqueQueue<int>();
        q.Enqueue(1);
        q.Enqueue(2);

        q.Clear();

        Assert.Equal(0, q.Count);
        Assert.False(q.Contains(1));
        q.Enqueue(1);
        Assert.Equal(1, q.Count);
    }

    [Fact]
    public void GetEnumeratorYieldsQueueOrder()
    {
        var q = new UniqueQueue<int>();
        q.Enqueue(5);
        q.Enqueue(6);
        q.Enqueue(7);

        var seen = new System.Collections.Generic.List<int>();
        foreach (var item in q)
        {
            seen.Add(item);
        }

        Assert.Equal(new[] { 5, 6, 7 }, seen);
    }
}
}
