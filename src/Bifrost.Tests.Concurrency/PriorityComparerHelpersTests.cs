// =============================================================================
// <copyright file="PriorityComparerHelpersTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Concurrency;

namespace Bifrost.Tests.Concurrency;

/// <summary>
/// Coverage for <see cref="PriorityComparerHelpers.InitializeComparer{TPriority}"/> (DR-4): the
/// comparer-normalization branches that pick the devirtualized default-comparer path (a stored
/// <see langword="null"/>) versus a materialized/explicit comparer, across the value-type and
/// reference-type priority axes.
/// </summary>
/// <remarks>
/// <para>
/// The normalization rule is observable two ways. Directly via
/// <see cref="PriorityComparerHelpers.InitializeComparer{TPriority}"/> (the helper is internal and
/// visible to this project), and indirectly via <see cref="SubQueue{TElement, TPriority}"/>'s
/// <c>UsesDefaultComparerPath</c> (true exactly when the stored comparer is null), which proves the
/// queue actually dual-paths on the normalized result rather than the helper being dead.
/// </para>
/// <para>
/// <b>Non-tautology.</b> Each assertion fixes the expected stored value (null vs the exact comparer
/// instance) for a specific (priority-kind, supplied-comparer) pair; a normalization that collapsed
/// the cases — e.g. always materializing, or never devirtualizing — would fail at least one.
/// </para>
/// </remarks>
public class PriorityComparerHelpersTests
{
    /// <summary>
    /// A value-type priority whose effective comparer is <see cref="Comparer{T}.Default"/> (supplied
    /// as either <see langword="null"/> or the Default instance itself) normalizes to a stored
    /// <see langword="null"/> — selecting the devirtualized default-comparer hot path.
    /// </summary>
    [Test]
    public async Task InitializeComparer_ValueTypeDefault_NormalizesToNull()
    {
        // Supplying null for a value type with default ordering -> null (devirtualized path).
        await Assert.That(PriorityComparerHelpers.InitializeComparer<int>(null)).IsNull().Because(
            "a null comparer on a value-type priority selects the devirtualized default path (stored null)");

        // Supplying Comparer<int>.Default explicitly -> also null (it IS the default).
        await Assert.That(PriorityComparerHelpers.InitializeComparer<int>(Comparer<int>.Default)).IsNull().Because(
            "passing Comparer<int>.Default for a value type collapses to the same devirtualized null path");
    }

    /// <summary>
    /// A value-type priority with a CUSTOM (non-default) comparer keeps that exact comparer — the
    /// devirtualized path is not available, so the explicit comparer is stored as-is.
    /// </summary>
    [Test]
    public async Task InitializeComparer_ValueTypeCustom_KeepsExplicitComparer()
    {
        var custom = new DescendingIntComparer();

        IComparer<int>? stored = PriorityComparerHelpers.InitializeComparer<int>(custom);

        await Assert.That(stored).IsNotNull().Because("a custom comparer on a value type is not devirtualizable");
        await Assert.That(ReferenceEquals(stored, custom)).IsTrue().Because(
            "the exact custom comparer instance is stored unchanged");
    }

    /// <summary>
    /// A reference-type priority with a <see langword="null"/> comparer MATERIALIZES
    /// <see cref="Comparer{T}.Default"/> — reference types cannot devirtualize the default
    /// (dotnet/runtime#10050), so the cached-field path needs a concrete comparer, never null.
    /// </summary>
    [Test]
    public async Task InitializeComparer_ReferenceTypeNull_MaterializesDefault()
    {
        IComparer<string>? stored = PriorityComparerHelpers.InitializeComparer<string>(null);

        await Assert.That(stored).IsNotNull().Because(
            "a reference-type priority cannot devirtualize the default, so null materializes to Comparer<string>.Default");
        await Assert.That(ReferenceEquals(stored, Comparer<string>.Default)).IsTrue().Because(
            "the materialized comparer is exactly Comparer<string>.Default");
    }

    /// <summary>
    /// A reference-type priority with a custom comparer keeps that exact comparer (the
    /// <c>comparer ?? Default</c> arm where the left operand is non-null).
    /// </summary>
    [Test]
    public async Task InitializeComparer_ReferenceTypeCustom_KeepsExplicitComparer()
    {
        var custom = StringComparer.OrdinalIgnoreCase;

        IComparer<string>? stored = PriorityComparerHelpers.InitializeComparer<string>(custom);

        await Assert.That(ReferenceEquals(stored, custom)).IsTrue().Because(
            "an explicit comparer on a reference-type priority is stored unchanged");
    }

    /// <summary>
    /// The normalization is observable end-to-end through the sub-queue: a value-type/default queue
    /// reports it uses the devirtualized default path, a value-type/custom queue does not, and a
    /// reference-type/null queue does not (it materialized Default). This proves the queue genuinely
    /// dual-paths on the normalized result.
    /// </summary>
    [Test]
    public async Task SubQueue_UsesDefaultComparerPath_ReflectsNormalization()
    {
        var defaultValueType = new SubQueue<int, int>(comparer: null, index: 0, occupancy: new ulong[1]);
        var customValueType = new SubQueue<int, int>(comparer: new DescendingIntComparer(), index: 0, occupancy: new ulong[1]);
        var nullReferenceType = new SubQueue<int, string>(comparer: null, index: 0, occupancy: new ulong[1]);

        await Assert.That(defaultValueType.UsesDefaultComparerPath).IsTrue().Because(
            "a value-type/default sub-queue stores null and uses the devirtualized path");
        await Assert.That(customValueType.UsesDefaultComparerPath).IsFalse().Because(
            "a value-type/custom sub-queue stores the explicit comparer, not null");
        await Assert.That(nullReferenceType.UsesDefaultComparerPath).IsFalse().Because(
            "a reference-type sub-queue materialized Comparer<string>.Default, so the stored comparer is non-null");
    }

    /// <summary>
    /// The normalization makes the queue's public <c>Comparer</c> never null while still honoring a
    /// custom comparer's ORDERING: a value-type queue built with a descending comparer drains in
    /// descending order, proving the custom comparer flows through the dual path correctly.
    /// </summary>
    [Test]
    public async Task CustomComparer_ChangesDrainOrder_AndComparerPropertyIsNeverNull()
    {
        var queue = new ConcurrentPriorityQueue<int, int>(subQueueCount: 8, boundedCapacity: -1, comparer: new DescendingIntComparer());
        int[] values = [3, 1, 4, 1, 5, 9, 2, 6];
        foreach (int v in values)
        {
            queue.Enqueue(v, v);
        }

        // A descending comparer makes the strict-min path drain LARGEST-first.
        int previous = int.MaxValue;
        int popped = 0;
        while (queue.TryDequeueMin(out _, out int priority))
        {
            await Assert.That(priority).IsLessThanOrEqualTo(previous).Because(
                "the descending comparer makes the strict-min drain return non-increasing priorities");
            previous = priority;
            popped++;
        }

        await Assert.That(popped).IsEqualTo(values.Length).Because("the full population drained");

        // The public Comparer property projects the (non-null) stored custom comparer.
        await Assert.That(queue.Comparer).IsNotNull().Because("the public Comparer property is never null");

        // A default-comparer queue's public Comparer projects the stored null back to Default.
        var defaultQueue = new ConcurrentPriorityQueue<int, int>();
        await Assert.That(ReferenceEquals(defaultQueue.Comparer, Comparer<int>.Default)).IsTrue().Because(
            "a default-comparer queue projects its stored null back to Comparer<int>.Default");
    }

    /// <summary>A custom comparer that orders integers in descending order, used to prove custom ordering flows through.</summary>
    private sealed class DescendingIntComparer : IComparer<int>
    {
        /// <inheritdoc/>
        public int Compare(int x, int y) => y.CompareTo(x);
    }
}
