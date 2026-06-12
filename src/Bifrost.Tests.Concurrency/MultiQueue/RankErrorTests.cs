// =============================================================================
// <copyright file="RankErrorTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================
// Ported from lvlup-sw/DataFerry@2bf0456 (src/DataFerry.Tests/Concurrency/MultiQueue/RankErrorTests.cs)

using Bifrost.Concurrency;

namespace Bifrost.Tests.Concurrency.MultiQueue;

/// <summary>
/// The empirical rank-error distribution gate (DR-16): the anti-F-7 regression test. It verifies
/// single-threaded that the MultiQueue's relaxed <see cref="ConcurrentPriorityQueue{TElement, TPriority}.TryDequeue"/>
/// returns elements whose RANK (position in the sorted order of the elements present at the moment
/// of the pop; rank 0 = the true minimum) follows the two-choice quality bound, and — the F-7
/// lesson — that it would CATCH an inverted (max-heap) implementation.
/// </summary>
/// <remarks>
/// <para>
/// <b>The F-7 lesson.</b> The v1 SprayList returned the MAXIMUM (an inverted heap order) and no
/// test caught it. These tests exist to make that failure mode impossible to ship silently:
/// they were each validated against a deliberately inverted (max-heap) build via the kill-probe
/// recorded in the upstream task notes — every one fails decisively (observed mean rank near
/// N/2 ≈ 5000 versus the bound ≈ 27 for n=16) — and pass against the real min-heap build.
/// </para>
/// <para>
/// <b>The theoretical bound.</b> With two-choice sampling over <c>n</c> sub-queues, the expected
/// rank of a dequeued element is ≈ <c>(5/6)·n</c> in steady state (the ESA 2025 exact result), and
/// with stickiness it scales roughly linearly to ≈ <c>(5/6)·n·s</c> — so the looseness compounds
/// across <i>both</i> the sub-queue count and the stickiness dial. The assertions here use
/// deliberately GENEROUS multiples of that <c>s</c>-aware bound (2× on the mean, 10× on the P99) so
/// that ordinary statistical variation never flakes while an inversion — which inflates the mean by
/// two to three orders of magnitude — is caught with enormous margin at every <c>(n, s)</c>. A
/// companion monotonicity test asserts the mean genuinely rises from <c>s=1</c> to <c>s=4</c>, so the
/// gate would also catch a dequeue that silently ignored <c>s</c>.
/// </para>
/// <para>
/// <b>Rank measurement is O(log N) per pop.</b> The population is N distinct integer priorities
/// <c>0..N−1</c> with <c>element == priority</c>. A Fenwick tree (<see cref="BinaryIndexedTree"/>)
/// tracks priority presence: all N are inserted up front; on each pop of priority <c>p</c> its true
/// rank is the number of still-present priorities strictly smaller than <c>p</c>
/// (<c>bit.PrefixSum(p − 1)</c>), after which <c>p</c> is removed. The whole 10^4-pop drain runs in
/// well under a second.
/// </para>
/// <para>
/// <b>Tuning rule.</b> The numeric bounds below may be tuned only with a documented statistical
/// rationale (e.g. a revised theoretical constant), NEVER to mask a regression. A failure here is
/// the gate working — investigate the dequeue path before touching a constant.
/// </para>
/// <para>
/// <b>Measured duration.</b> On the upstream development machine the full test class completes in
/// roughly 0.3 s (each n=16 drain of 10^4 pops is a few tens of milliseconds plus the BIT).
/// </para>
/// </remarks>
public class RankErrorTests
{
    /// <summary>The population size drained per scenario: 10^4 distinct priorities <c>0..N−1</c>.</summary>
    private const int Population = 10_000;

    /// <summary>The fixed shuffle seed so every drain is bit-for-bit reproducible.</summary>
    private const int ShuffleSeed = 0xC0FFEE;

    /// <summary>
    /// The theoretical expected rank per the ESA 2025 two-choice result, scaled by stickiness:
    /// <c>(5/6)·n·s</c>. Stickiness reuses the sampled pair for <c>s</c> ops, multiplying the
    /// relaxation roughly linearly in <c>s</c> (so the looseness compounds across both the
    /// sub-queue count <i>and</i> the stickiness dial).
    /// </summary>
    private static double TheoreticalMeanRank(int subQueueCount, int stickiness) => (5.0 / 6.0) * subQueueCount * stickiness;

    /// <summary>
    /// The core empirical gate: drains a 10^4-element population and asserts the observed mean and
    /// P99 rank sit inside generous multiples (2× mean, 10× P99) of the <c>(5/6)·n·s</c> theoretical
    /// bound at each pinned <c>(subQueueCount, stickiness)</c>.
    /// </summary>
    /// <param name="subQueueCount">The exact sub-queue count <c>n</c> pinned via the internal constructor.</param>
    /// <param name="stickiness">The stickiness factor <c>s</c> pinned via the internal constructor.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Test]
    [Arguments(4, 1)]
    [Arguments(16, 1)]
    [Arguments(16, 4)]
    public async Task TryDequeue_SingleThreadDrain_EmpiricalMeanRankWithinTheoreticalBound(int subQueueCount, int stickiness)
    {
        // Drain the full population recording every pop's true rank, then assert the empirical mean
        // and P99 sit within the generous multiples of the (5/6)·n·s theoretical mean. For an inverted
        // (max-heap) build every pop is near the top of the remaining population, so the mean lands
        // near N/2 ≈ 5000 — orders of magnitude above either bound at every (n, s).
        int[] ranks = DrainAndMeasureRanks(subQueueCount, stickiness);

        double meanRank = ranks.Average();
        int p99Rank = Percentile(ranks, 0.99);

        double meanBound = 2.0 * TheoreticalMeanRank(subQueueCount, stickiness);   // ×s-aware
        double p99Bound = 10.0 * subQueueCount * stickiness;                       // ×s-aware

        await Assert.That(ranks.Length).IsEqualTo(Population).Because(
            "the drain must record exactly one rank per element in the population");

        await Assert.That(meanRank).IsLessThanOrEqualTo(meanBound).Because(
            $"empirical mean rank {meanRank:F2} exceeded 2×(5/6)×n×s = {meanBound:F2} for n={subQueueCount}, s={stickiness}; " +
            $"theoretical mean is (5/6)·n·s = {TheoreticalMeanRank(subQueueCount, stickiness):F2} (an inversion would land near {Population / 2})");

        await Assert.That((double)p99Rank).IsLessThanOrEqualTo(p99Bound).Because(
            $"empirical P99 rank {p99Rank} exceeded 10×n×s = {p99Bound:F0} for n={subQueueCount}, s={stickiness}");
    }

    /// <summary>
    /// The stickiness-sensitivity check: at a fixed sub-queue count the empirical mean rank under
    /// <c>s=4</c> must exceed that under <c>s=1</c>, so a dequeue that silently ignored <c>s</c>
    /// would be caught.
    /// </summary>
    /// <returns>The asynchronous test operation.</returns>
    [Test]
    public async Task TryDequeue_StickinessIncreasesMeanRank_Monotonic()
    {
        // The gate must be SENSITIVE to stickiness: looseness grows with s. At a fixed sub-queue count
        // the empirical mean rank under s=4 must exceed that under s=1 (the linear-in-s relaxation),
        // proving stickiness genuinely trades quality for throughput rather than silently doing nothing
        // — and that the gate would catch a build where the dequeue ignored s.
        const int subQueueCount = 16;

        double meanS1 = DrainAndMeasureRanks(subQueueCount, stickiness: 1).Average();
        double meanS4 = DrainAndMeasureRanks(subQueueCount, stickiness: 4).Average();

        await Assert.That(meanS4).IsGreaterThan(meanS1).Because(
            $"mean rank must rise with stickiness: s=1 gave {meanS1:F2}, s=4 gave {meanS4:F2}");
    }

    /// <summary>
    /// The explicit anti-F-7 inversion alarm: fewer than 1% of pops may land in the top decile of
    /// the remaining population; an inverted (max-heap) build would put essentially 100% there.
    /// </summary>
    /// <param name="stickiness">The stickiness factor <c>s</c> to pin via the internal constructor.</param>
    /// <returns>The asynchronous test operation.</returns>
    [Test]
    [Arguments(1)]
    [Arguments(8)]
    public async Task TryDequeue_RankDistribution_NotPointMassedAtMaximum(int stickiness)
    {
        // The explicit anti-F-7 inversion alarm, verified to still fire across stickiness levels. For a
        // correct two-choice min-heap, popping near the TOP of the remaining population is vanishingly
        // rare even at s=8 (mean ≈ (5/6)·16·8 ≈ 107 ≪ N/2); for the v1-style inversion EVERY pop is at
        // (or near) the maximum, so this fraction would be ~100%. We require < 1% at every s.
        const int subQueueCount = 16;
        int[] ranks = DrainAndMeasureRanks(subQueueCount, stickiness);

        // At the i-th pop (0-based) the remaining population — including the element being popped —
        // is Population − i. A rank in the top decile means rank ≥ 0.9 × that remaining count.
        long topDecilePops = 0;
        for (int i = 0; i < ranks.Length; i++)
        {
            int remaining = Population - i;
            if (ranks[i] >= 0.9 * remaining)
            {
                topDecilePops++;
            }
        }

        double fractionTopDecile = (double)topDecilePops / ranks.Length;

        await Assert.That(fractionTopDecile).IsLessThan(0.01).Because(
            $"{topDecilePops} of {ranks.Length} pops ({fractionTopDecile:P2}) landed in the top decile of the " +
            "remaining population — a two-choice min-heap pops near the minimum, so this signals an inverted heap (F-7)");
    }

    /// <summary>
    /// The distribution-shape check (pinned at <c>s=1</c>): bucketing ranks into the first four
    /// <c>n</c>-wide buckets, the counts must strictly decrease — two-choice rank mass concentrates
    /// near zero and decays, which an inverted build cannot reproduce.
    /// </summary>
    /// <returns>The asynchronous test operation.</returns>
    [Test]
    public async Task TryDequeue_RankDistribution_DecaysGeometrically()
    {
        // A generous shape check: two-choice rank mass concentrates near 0 and decays. Bucketing
        // ranks into [0,n) [n,2n) [2n,3n) [3n,4n), the counts must be strictly decreasing across the
        // first four buckets. An inverted build puts essentially no mass in these low buckets at all
        // (every rank is near N/2), so the monotone-decay relation collapses.
        //
        // Deliberately pinned at s = 1: a sticky pair drains s ranks in a row, lumping the low-rank
        // mass, so the clean monotone-decay shape is only asserted for the default path. Coverage
        // for s > 1 comes from the s-aware mean/P99 bounds, the monotonicity test, and the anti-F-7
        // alarm (parameterized up to s = 8) above.
        const int subQueueCount = 16;
        int[] ranks = DrainAndMeasureRanks(subQueueCount, stickiness: 1);

        long[] buckets = new long[4];
        foreach (int rank in ranks)
        {
            int bucket = rank / subQueueCount;
            if (bucket < buckets.Length)
            {
                buckets[bucket]++;
            }
        }

        await Assert.That(buckets[0]).IsGreaterThan(buckets[1]).Because(
            $"bucket [0,{subQueueCount}) count {buckets[0]} must exceed [{subQueueCount},{2 * subQueueCount}) count {buckets[1]}");
        await Assert.That(buckets[1]).IsGreaterThan(buckets[2]).Because(
            $"bucket [{subQueueCount},{2 * subQueueCount}) count {buckets[1]} must exceed [{2 * subQueueCount},{3 * subQueueCount}) count {buckets[2]}");
        await Assert.That(buckets[2]).IsGreaterThan(buckets[3]).Because(
            $"bucket [{2 * subQueueCount},{3 * subQueueCount}) count {buckets[2]} must exceed [{3 * subQueueCount},{4 * subQueueCount}) count {buckets[3]}");
    }

    /// <summary>
    /// The single shared drain used by all the tests so they cannot diverge on how ranks are
    /// produced. Populates a <c>ConcurrentPriorityQueue&lt;int,int&gt;</c> of <see cref="Population"/>
    /// distinct priorities <c>0..N−1</c> (element == priority) inserted in a fixed-seed shuffled
    /// order, then drains the whole queue via <see cref="ConcurrentPriorityQueue{TElement, TPriority}.TryDequeue"/>,
    /// recording each pop's true rank through a Fenwick tree over priority presence (O(log N) per
    /// pop). Returns the rank sequence in pop order.
    /// </summary>
    /// <param name="subQueueCount">The exact sub-queue count to pin via the internal constructor.</param>
    /// <param name="stickiness">The stickiness factor <c>s</c> to pin via the internal constructor.</param>
    /// <returns>The true rank of every popped element, in the order popped.</returns>
    private static int[] DrainAndMeasureRanks(int subQueueCount, int stickiness)
    {
        var queue = new ConcurrentPriorityQueue<int, int>(subQueueCount, boundedCapacity: -1, comparer: null, stickiness: stickiness);

        // Distinct priorities 0..N-1, shuffled deterministically so the relaxation sees a realistic
        // (not pre-sorted) scatter across sub-queues while staying reproducible.
        int[] priorities = Enumerable.Range(0, Population).ToArray();
        Shuffle(priorities, ShuffleSeed);

        // The BIT covers priorities 1..Population (1-based internally); every priority starts present.
        var presence = new BinaryIndexedTree(Population);
        foreach (int p in priorities)
        {
            queue.Enqueue(p, p);
            presence.Add(p, 1);
        }

        var ranks = new int[Population];
        int popCount = 0;

        while (queue.TryDequeue(out int element, out int priority))
        {
            // element == priority by construction.
            if (element != priority)
            {
                throw new InvalidOperationException(
                    $"element ({element}) and priority ({priority}) diverged — the drain invariant is broken.");
            }

            // True rank = number of still-present priorities strictly smaller than `priority`.
            ranks[popCount++] = presence.PrefixSum(priority - 1);

            // Remove this priority from the presence set so later ranks reflect the shrinking pool.
            presence.Add(priority, -1);
        }

        if (popCount != Population)
        {
            throw new InvalidOperationException(
                $"drained {popCount} elements but expected {Population} — TryDequeue lost or duplicated entries.");
        }

        return ranks;
    }

    /// <summary>Deterministic Fisher-Yates shuffle so every drain is reproducible.</summary>
    /// <param name="array">The array to shuffle in place.</param>
    /// <param name="seed">The RNG seed.</param>
    private static void Shuffle(int[] array, int seed)
    {
        var rng = new Random(seed);
        for (int i = array.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (array[i], array[j]) = (array[j], array[i]);
        }
    }

    /// <summary>
    /// Returns the value at the given percentile of <paramref name="values"/> (nearest-rank, on a
    /// sorted copy). Used for the P99 bound; never mutates the caller's array.
    /// </summary>
    /// <param name="values">The rank samples.</param>
    /// <param name="percentile">The percentile in <c>[0, 1]</c>.</param>
    /// <returns>The value at that percentile.</returns>
    private static int Percentile(int[] values, double percentile)
    {
        int[] sorted = (int[])values.Clone();
        Array.Sort(sorted);

        // Nearest-rank: index = ceil(p × N) − 1, clamped into range.
        int index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        index = Math.Clamp(index, 0, sorted.Length - 1);
        return sorted[index];
    }

    /// <summary>
    /// A minimal Fenwick tree (binary indexed tree) over integer priorities <c>0..maxValue−1</c>,
    /// used to compute each popped element's true rank in O(log N). It supports point updates
    /// (insert/remove a priority's presence) and prefix sums (count of present priorities ≤ a
    /// threshold). Internally 1-based; priority <c>p</c> maps to slot <c>p + 1</c> so that
    /// <c>PrefixSum(−1)</c> (no priority is smaller than the global minimum) returns 0 cleanly.
    /// </summary>
    private sealed class BinaryIndexedTree
    {
        private readonly int[] _tree;
        private readonly int _length;

        /// <summary>
        /// Initializes a new instance of the <see cref="BinaryIndexedTree"/> class covering
        /// priorities <c>0..maxValue−1</c>.
        /// </summary>
        /// <param name="maxValue">The exclusive upper bound on priority values (the population size).</param>
        internal BinaryIndexedTree(int maxValue)
        {
            // Slots 1..maxValue map priorities 0..maxValue-1 (priority p -> slot p+1).
            _length = maxValue;
            _tree = new int[maxValue + 1];
        }

        /// <summary>Adds <paramref name="delta"/> to the presence count of priority <paramref name="priority"/>.</summary>
        /// <param name="priority">The priority value in <c>0..maxValue−1</c>.</param>
        /// <param name="delta">The signed change (<c>+1</c> to insert, <c>−1</c> to remove).</param>
        internal void Add(int priority, int delta)
        {
            for (int i = priority + 1; i <= _length; i += i & -i)
            {
                _tree[i] += delta;
            }
        }

        /// <summary>
        /// Returns the number of present priorities with value <c>≤ threshold</c> — i.e. the count of
        /// elements not greater than <paramref name="threshold"/>. Passing <c>priority − 1</c> yields
        /// the count of present priorities STRICTLY smaller than <c>priority</c>, which is its rank.
        /// </summary>
        /// <param name="threshold">The inclusive upper bound; <c>−1</c> returns 0.</param>
        /// <returns>The count of present priorities <c>≤ threshold</c>.</returns>
        internal int PrefixSum(int threshold)
        {
            int sum = 0;
            for (int i = threshold + 1; i > 0; i -= i & -i)
            {
                sum += _tree[i];
            }

            return sum;
        }
    }
}
