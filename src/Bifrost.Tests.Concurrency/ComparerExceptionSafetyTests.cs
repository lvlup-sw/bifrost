// =============================================================================
// <copyright file="ComparerExceptionSafetyTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Concurrency;

namespace Bifrost.Tests.Concurrency;

/// <summary>Exhaustive comparer failure-position regressions.</summary>
public class ComparerExceptionSafetyTests
{
    /// <summary>Checks rollback across heap sifts, eviction, flush and refill.</summary>
    /// <param name="bufferCapacity">The stripe buffer capacity.</param>
    /// <param name="defaultComparer">Whether a user-defined value priority throws from CompareTo.</param>
    /// <param name="bounded">Whether enqueue must reserve and recover bounded capacity.</param>
    [Test]
    [Arguments(0, false, true)]
    [Arguments(1, false, true)]
    [Arguments(16, false, true)]
    [Arguments(0, true, true)]
    [Arguments(1, true, true)]
    [Arguments(16, true, true)]
    [Arguments(0, false, false)]
    [Arguments(1, false, false)]
    [Arguments(16, false, false)]
    [Arguments(0, true, false)]
    [Arguments(1, true, false)]
    [Arguments(16, true, false)]
    public void EveryComparisonFailurePreservesContentsAndCapacity(int bufferCapacity, bool defaultComparer, bool bounded)
    {
        if (defaultComparer)
            VerifyFailures<string, ThrowingPriority>(bufferCapacity, bounded, null, (key, fault) => new(key, fault), id => id.ToString());
        else
            VerifyFailures<string, int>(bufferCapacity, bounded, fault => fault, (key, _) => key, id => id.ToString());
    }

    /// <summary>Exercises comparer rollback with compact value entries as well as the reference-entry matrix.</summary>
    /// <param name="bufferCapacity">The stripe buffer capacity.</param>
    /// <param name="bounded">Whether enqueue must reserve and recover bounded capacity.</param>
    [Test]
    [Arguments(0, true)]
    [Arguments(1, true)]
    [Arguments(16, true)]
    [Arguments(0, false)]
    [Arguments(1, false)]
    [Arguments(16, false)]
    public void ValueEntriesPreserveContentsAndCapacity(int bufferCapacity, bool bounded) =>
        VerifyFailures<int, int>(bufferCapacity, bounded, fault => fault, (key, _) => key, id => id);

    /// <summary>A refill with a sole remaining entry cannot invoke an arbitrary comparer.</summary>
    /// <param name="bufferCapacity">The stripe buffer capacity.</param>
    [Test]
    [Arguments(1)]
    [Arguments(16)]
    public void SingleEntryRefillDoesNotInvokeComparer(int bufferCapacity)
    {
        var comparer = new InjectedComparer();
        var stripe = new SubQueue<string, int>(comparer, 0, new ulong[1], bufferCapacity);
        stripe.TryLockedPush("first", 0);
        stripe.TryLockedPush("second", 1);
        comparer.Remaining = 1;
        Check(stripe.TryLockedPop(out string? value, out _) == SubQueuePopStatus.Success && value == "first", "single insertion refill");
        Check(comparer.Remaining == 1, "comparison-free insertion refill");
        Check(stripe.VolatileCount == 1 && stripe.TryLockedPop(out value, out _) == SubQueuePopStatus.Success && value == "second", "drain after refill");

        if (bufferCapacity == 1)
        {
            comparer.Remaining = int.MaxValue;
            stripe.TryLockedPush("first", 0);
            stripe.TryLockedPush("second", 1);
            stripe.TryLockedPush("third", 2);
            stripe.TryLockedPop(out _, out _);
            Check(stripe.HeapSize == 1 && stripe.InsertionCountForTest == 0, "sole remaining heap entry");
            comparer.Remaining = 1;
            Check(stripe.TryLockedPop(out value, out _) == SubQueuePopStatus.Success && value == "second", "single heap refill");
            Check(comparer.Remaining == 1, "comparison-free heap refill");
        }
    }

    /// <summary>Two remaining entries still require rollback if refill compares them.</summary>
    /// <param name="bufferCapacity">The stripe buffer capacity.</param>
    [Test]
    [Arguments(1)]
    [Arguments(16)]
    public void TwoEntryRefillPreservesContentsOnComparerFailure(int bufferCapacity)
    {
        var comparer = new InjectedComparer();
        var stripe = new SubQueue<string, int>(comparer, 0, new ulong[1], bufferCapacity);
        stripe.TryLockedPush("first", 0);
        stripe.TryLockedPush("second", 1);
        stripe.TryLockedPush("third", 2);
        Check(stripe.VolatileCount == 3 && stripe.DeletionCountForTest == 1, "refill rollback boundary");
        uint version = stripe.DebugTopVersionForTest;
        int insertion = stripe.InsertionCountForTest, heap = stripe.HeapSize;
        comparer.Remaining = 1;
        bool threw = false;
        try
        {
            stripe.TryLockedPop(out _, out _);
        }
        catch (ComparisonFailure)
        {
            threw = true;
        }

        Check(threw, "refill comparison failure");
        Check(stripe.VolatileCount == 3 && stripe.DeletionCountForTest == 1 &&
            stripe.InsertionCountForTest == insertion && stripe.HeapSize == heap, "refill rollback counts");
        Check(stripe.DebugTopVersionForTest == version, "refill rollback publication");
        comparer.Remaining = int.MaxValue;
        foreach (string expected in new[] { "first", "second", "third" })
            Check(stripe.TryLockedPop(out string? value, out _) == SubQueuePopStatus.Success && value == expected, "refill rollback drain");
        Check(stripe.VolatileCount == 0, "refill rollback final count");
    }

    private static void VerifyFailures<TElement, TPriority>(int bufferCapacity, bool bounded,
        Func<InjectedComparer, IComparer<TPriority>>? createComparer,
        Func<int, InjectedComparer, TPriority> createPriority, Func<int, TElement> createElement)
    {
        // Rebuild identical prefixes and throw at every comparison in the next operation.
        // Ascending fills, descending eviction cascades, and a full drain exercise I, D and heap.
        var operations = Enumerable.Range(0, 96).Select(i => (Push: true, Key: i))
            .Concat(Enumerable.Range(0, 32).Select(i => (Push: true, Key: -i - 1)))
            .Concat(Enumerable.Range(0, 128).Select(i => (Push: false, Key: i))).ToArray();
        int failures = 0;
        for (int prefix = 0; prefix < operations.Length; prefix++)
        {
            for (int position = 1; position < 2048; position++)
            {
                var comparer = new InjectedComparer();
                var queue = ConcurrentPriorityQueue<TElement, TPriority>.Create(new()
                {
                    Capacity = bounded ? 160 : null, SubQueueCount = 1, BufferCapacity = bufferCapacity, Comparer = createComparer?.Invoke(comparer),
                });
                for (int i = 0; i < prefix; i++) Apply(queue, operations[i], i, key => createPriority(key, comparer), createElement);
                var before = queue.ToArray().OrderBy(x => x.Priority).ToArray();
                var stripe = queue.SubQueuesForTest[0];
                uint version = stripe.DebugTopVersionForTest;
                int insertion = stripe.InsertionCountForTest, deletion = stripe.DeletionCountForTest;
                comparer.Remaining = position;
                bool threw = false;
                try
                {
                    Apply(queue, operations[prefix], prefix, key => createPriority(key, comparer), createElement);
                }
                catch (ComparisonFailure)
                {
                    threw = true;
                }

                if (!threw) break;
                failures++;
                comparer.Remaining = int.MaxValue;
                Check(queue.Count == before.Length && queue.DebugBoundedCountForTest == (bounded ? before.Length : 0), "counts");
                Check(queue.ToArray().OrderBy(x => x.Priority).SequenceEqual(before), "contents");
                Check(stripe.DebugTopVersionForTest == version, "publication");
                Check(stripe.InsertionCountForTest == insertion && stripe.DeletionCountForTest == deletion, "buffers");
                foreach (var expected in before)
                {
                    Check(queue.TryDequeue(out TElement? element, out TPriority? priority), "drain availability");
                    Check(EqualityComparer<TElement>.Default.Equals(element, expected.Element) && EqualityComparer<TPriority>.Default.Equals(priority, expected.Priority), "heap order");
                }

                queue.Clear();
                Check(queue.DebugBoundedCountForTest == 0, "clear reservation");
                for (int i = 0; i < 160; i++) Check(queue.TryEnqueue(createElement(i), createPriority(i, comparer)), "refill capacity");
                if (bounded) Check(!queue.TryEnqueue(createElement(161), createPriority(161, comparer)), "hard bound");
                else Check(queue.TryEnqueue(createElement(161), createPriority(161, comparer)), "unbounded admission");
            }
        }

        Check(failures > 100, "failure injection coverage");
    }

    private static void Apply<TElement, TPriority>(ConcurrentPriorityQueue<TElement, TPriority> queue, (bool Push, int Key) operation, int id, Func<int, TPriority> priority, Func<int, TElement> element)
    {
        if (operation.Push) queue.Enqueue(element(id), priority(operation.Key));
        else queue.TryDequeue(out _, out _);
    }

    private static void Check(bool condition, string invariant)
    {
        if (!condition) throw new InvalidOperationException($"Comparer exception violated {invariant}.");
    }

    private readonly record struct ThrowingPriority(int Value, InjectedComparer Fault) : IComparable<ThrowingPriority>
    {
        public int CompareTo(ThrowingPriority other) => Fault.Compare(Value, other.Value);
    }

    private sealed class ComparisonFailure : Exception;

    private sealed class InjectedComparer : IComparer<int>
    {
        public int Remaining { get; set; } = int.MaxValue;
        public int Compare(int x, int y)
        {
            if (--Remaining == 0) throw new ComparisonFailure();
            return x.CompareTo(y);
        }
    }
}
