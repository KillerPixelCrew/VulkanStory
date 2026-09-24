// Source: Optimum.Render.Vulkan.Tests/ValidationAssert.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using System.Linq;
using Optimum.Render.Vulkan.Core;
using Xunit;

/// <summary>
/// Shared check that a captured validation-layer log holds no errors.
/// </summary>
internal static partial class ValidationAssert
{
    /// <summary>
    /// Fails when any message carries the error prefix. Only what the layers
    /// reported at error severity counts: advisories - a fragment output with
    /// no attachment, say - are prefixed as warnings and are not failures;
    /// treating every message as one made these assertions fire on notes
    /// about correct frames. Synchronization hazards are judged by
    /// <see cref="NoSyncHazards" /> against the pinned list instead, so a known
    /// renderer hazard does not fail every test that happens to trigger it.
    /// </summary>
    public static void NoErrors(IReadOnlyCollection<string> messages)
    {
        var errors = Snapshot(messages)
            .Where(m => m.StartsWith(VulkanContext.ErrorPrefix, StringComparison.Ordinal) && !IsSynchronization(m))
            .ToList();
        Assert.True(errors.Count == 0, "validation errors:\n" + string.Join("\n", errors));
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/KnownSyncHazards.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;

/// <summary>A synchronization-validation message a test is known to produce today.</summary>
/// <param name="Id">The layer's message id, e.g. SYNC-HAZARD-WRITE-AFTER-WRITE.</param>
/// <param name="TestClass">The test class that produces it.</param>
/// <param name="TestMethod">The test method that produces it.</param>
/// <param name="Defect">The backend defect behind it.</param>
/// <param name="RetiredBy">The plan phase that removes the defect: "1B" or "2".</param>
internal sealed record KnownSyncHazard(string Id, string TestClass, string TestMethod, string Defect, string RetiredBy);

/// <summary>
/// Synchronization hazards the renderer produces today, pinned per test so a
/// new one fails (<see cref="ValidationAssert.NoSyncHazards" />) and a fixed
/// one must be deleted (<see cref="SyncHazardLedgerTests" />): the list can
/// only shrink. Nothing here is fixed by editing the list; each entry is
/// retired by the plan phase it names.
/// </summary>
internal static class KnownSyncHazards
{
    public static readonly KnownSyncHazard[] Entries =
    {
    };

    public static bool Covers(string id, string testClass, string testMethod)
    {
        foreach (KnownSyncHazard entry in Entries)
        {
            if (entry.Id == id && entry.TestClass == testClass && entry.TestMethod == testMethod) return true;
        }
        return false;
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/ValidationAssert.SyncHazards.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Optimum.Render.Vulkan.Core;
using Xunit;

internal static partial class ValidationAssert
{
    private const string WarningPrefix = "[warning] ";

    /// <summary>A copy taken under the list's lock; the layers append from other threads.</summary>
    public static List<string> Snapshot(IReadOnlyCollection<string> messages)
    {
        lock (messages) return new List<string>(messages);
    }

    /// <summary>
    /// The layer's message id, from the bracket tag <see cref="VulkanContext" />
    /// puts after the severity: "[error] [SYNC-HAZARD-WRITE-AFTER-WRITE] text".
    /// </summary>
    public static string? MessageId(string message)
    {
        int start = 0;
        if (message.StartsWith(VulkanContext.ErrorPrefix, StringComparison.Ordinal)) start = VulkanContext.ErrorPrefix.Length;
        else if (message.StartsWith(WarningPrefix, StringComparison.Ordinal)) start = WarningPrefix.Length;

        if (start >= message.Length || message[start] != '[') return null;
        int end = message.IndexOf(']', start + 1);
        if (end < 0) return null;
        string id = message.Substring(start + 1, end - start - 1).Trim();
        return id.Length == 0 ? null : id;
    }

    /// <summary>Synchronization validation reports under SYNC-* ids (SYNC-HAZARD-WRITE-AFTER-WRITE and friends).</summary>
    public static bool IsSynchronization(string message) =>
        MessageId(message) is string id && id.StartsWith("SYNC-", StringComparison.Ordinal);

    public static bool IsBestPractices(string message) =>
        MessageId(message) is string id && id.Contains("BestPractices", StringComparison.Ordinal);

    /// <summary>
    /// Fails on any synchronization message that <see cref="KnownSyncHazards" />
    /// does not pin for the running test. Best-practices messages are counted
    /// into <see cref="SyncHazardLedger" /> and printed by the ledger test, never
    /// failed. Call it wherever <see cref="NoErrors" /> is called.
    /// </summary>
    public static void NoSyncHazards(IReadOnlyCollection<string> messages, [CallerFilePath] string callerFile = "")
    {
        List<string> snapshot = Snapshot(messages);
        (string testClass, string testMethod) = SyncHazardLedger.CurrentTest(callerFile);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unexpected = new Dictionary<string, (int Count, string First)>(StringComparer.Ordinal);
        foreach (string message in snapshot)
        {
            if (!IsSynchronization(message)) continue;
            string id = MessageId(message)!;
            seen.Add(id);
            if (KnownSyncHazards.Covers(id, testClass, testMethod)) continue;
            unexpected[id] = unexpected.TryGetValue(id, out var entry)
                ? (entry.Count + 1, entry.First)
                : (1, message);
        }

        SyncHazardLedger.Observe(testClass, testMethod, seen);
        SyncHazardLedger.Tally(messages, snapshot, testClass);

        if (unexpected.Count == 0) return;
        var report = new StringBuilder("unlisted synchronization hazards (id | class | method | count | first message):\n");
        foreach (KeyValuePair<string, (int Count, string First)> pair in unexpected)
        {
            report.Append(pair.Key).Append(" | ").Append(testClass).Append(" | ").Append(testMethod)
                .Append(" | ").Append(pair.Value.Count).Append(" | ").Append(pair.Value.First).Append('\n');
        }
        report.Append("A hazard in the renderer is pinned in KnownSyncHazards with its defect and retiring phase; ")
            .Append("a hazard in the test's own API use is fixed in the test.");
        Assert.Fail(report.ToString());
    }
}

/// <summary>
/// What synchronization validation actually reported during this test run, so
/// the pinned list can only shrink, and how often each best-practices check
/// fired.
/// </summary>
internal static class SyncHazardLedger
{
    private static readonly object Gate = new();
    private static readonly Dictionary<(string Class, string Method), HashSet<string>> Observed = new();
    private static readonly SortedDictionary<string, int> SyncCounts = new(StringComparer.Ordinal);
    private static readonly SortedDictionary<string, int> BestPracticesCounts = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<object, StrongBox<int>> Tallied = new();

    /// <summary>
    /// The ledger's own tests feed it synthetic messages; they are kept out of
    /// the counts and the printed summary.
    /// </summary>
    private static bool IsSelfTest(string testClass) =>
        testClass.StartsWith(nameof(SyncHazardLedgerTests), StringComparison.Ordinal);

    /// <summary>
    /// The xunit test method on the stack. Assertions are often made from a
    /// shared helper, so the caller is not necessarily the test.
    /// </summary>
    public static (string Class, string Method) CurrentTest(string callerFile)
    {
        StackFrame[] frames = new StackTrace(false).GetFrames();
        (string Class, string Method)? fallback = null;
        foreach (StackFrame frame in frames)
        {
            MethodBase? method = frame.GetMethod();
            if (method?.DeclaringType == null) continue;
            if (method.GetCustomAttributes(typeof(FactAttribute), inherit: true).Length > 0)
            {
                return (method.DeclaringType.Name, method.Name);
            }
            Type type = method.DeclaringType;
            if (fallback == null && type.Namespace == typeof(SyncHazardLedger).Namespace &&
                type.Name.EndsWith("Tests", StringComparison.Ordinal))
            {
                fallback = (type.Name, method.Name);
            }
        }

        // File names can change when suites are consolidated. A type and method
        // from the stack remain the same test identity after a source move.
        return fallback ?? ("<unknown test>", "?");
    }

    /// <summary>Notes that a test asserted, and which synchronization ids it had produced by then.</summary>
    public static void Observe(string testClass, string testMethod, IEnumerable<string> syncIds)
    {
        lock (Gate)
        {
            if (!Observed.TryGetValue((testClass, testMethod), out HashSet<string>? ids))
            {
                ids = new HashSet<string>(StringComparer.Ordinal);
                Observed[(testClass, testMethod)] = ids;
            }
            ids.UnionWith(syncIds);
        }
    }

    /// <summary>
    /// Counts messages by id. A test may assert more than once over the same
    /// growing list; each message is counted once.
    /// </summary>
    public static void Tally(object list, List<string> snapshot, string testClass)
    {
        if (IsSelfTest(testClass)) return;
        lock (Gate)
        {
            StrongBox<int> tallied = Tallied.GetValue(list, _ => new StrongBox<int>(0));
            for (int i = tallied.Value; i < snapshot.Count; i++)
            {
                string message = snapshot[i];
                string? id = ValidationAssert.MessageId(message);
                if (id == null) continue;
                if (ValidationAssert.IsSynchronization(message)) Increment(SyncCounts, id);
                else if (ValidationAssert.IsBestPractices(message)) Increment(BestPracticesCounts, id);
            }
            tallied.Value = Math.Max(tallied.Value, snapshot.Count);
        }
    }

    private static void Increment(SortedDictionary<string, int> counts, string id) =>
        counts[id] = counts.TryGetValue(id, out int count) ? count + 1 : 1;

    /// <summary>
    /// Entries whose test asserted in this run without the hazard occurring. A
    /// test that was filtered out, skipped or failed before its assertion never
    /// observes, so it cannot make an entry look stale.
    /// </summary>
    public static List<string> StaleEntries(IEnumerable<KnownSyncHazard> entries)
    {
        var stale = new List<string>();
        lock (Gate)
        {
            foreach (KnownSyncHazard entry in entries)
            {
                if (Observed.TryGetValue((entry.TestClass, entry.TestMethod), out HashSet<string>? ids)
                    && !ids.Contains(entry.Id))
                {
                    stale.Add(entry.Id + " | " + entry.TestClass + "." + entry.TestMethod);
                }
            }
        }
        return stale;
    }

    public static string Summary()
    {
        var text = new StringBuilder();
        lock (Gate)
        {
            var keys = new List<(string Class, string Method)>();
            foreach ((string Class, string Method) key in Observed.Keys)
            {
                if (!IsSelfTest(key.Class)) keys.Add(key);
            }
            keys.Sort((a, b) => string.CompareOrdinal(a.Class + "." + a.Method, b.Class + "." + b.Method));

            text.Append("tests that asserted: ").Append(keys.Count).Append('\n');
            text.Append("synchronization messages by id:\n");
            foreach (KeyValuePair<string, int> pair in SyncCounts)
                text.Append("  ").Append(pair.Key).Append(": ").Append(pair.Value).Append('\n');
            text.Append("best-practices messages by id:\n");
            foreach (KeyValuePair<string, int> pair in BestPracticesCounts)
                text.Append("  ").Append(pair.Key).Append(": ").Append(pair.Value).Append('\n');
            text.Append("synchronization ids by test:\n");
            foreach ((string Class, string Method) key in keys)
            {
                if (Observed[key].Count == 0) continue;
                var ids = new List<string>(Observed[key]);
                ids.Sort(StringComparer.Ordinal);
                text.Append("  ").Append(key.Class).Append('.').Append(key.Method).Append(": ")
                    .Append(string.Join(", ", ids)).Append('\n');
            }
        }
        return text.ToString();
    }
}
}
