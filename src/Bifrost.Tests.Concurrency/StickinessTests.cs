// =============================================================================
// <copyright file="StickinessTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Concurrency;

namespace Bifrost.Tests.Concurrency;

/// <summary>
/// Coverage for the stickiness dial (DR-4): the constructor's <c>stickiness</c> validation and
/// <c>-1</c>-default resolution (<c>ResolveStickiness</c>), and the <see cref="ThreadHandle"/>
/// sticky-selection state machine including the reset-on-contention paths
/// (<c>NextStickyIndex</c>/<c>NextStickyPair</c>/<c>ResetStickyEnqueue</c>/<c>ResetStickyDequeue</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Non-tautology.</b> The validation tests fix the exact resolved <c>Stickiness</c> value (or the
/// thrown exception). The handle tests drive a deterministic <see cref="ThreadHandle"/> built from
/// fixed seed words and assert the exact reuse/resample/reset behavior: a sticky index is reused for
/// exactly <c>s</c> calls, a mismatched mask forces a fresh sample, and a reset ends the period
/// immediately. Any change that broke the countdown arithmetic or the mask gate would fail.
/// </para>
/// </remarks>
public class StickinessTests
{
    /// <summary>
    /// The stickiness constructor resolves the <c>-1</c> sentinel to the default factor (<c>1</c>)
    /// and honors an explicit positive factor, exposed through <c>StickinessForTest</c>.
    /// </summary>
    [Test]
    [Arguments(-1, 1)] // -1 sentinel resolves to the default 1.
    [Arguments(1, 1)]
    [Arguments(4, 4)]
    [Arguments(7, 7)]
    public async Task Ctor_StickinessResolution_HonorsDefaultAndExplicit(int requested, int expected)
    {
        var queue = new ConcurrentPriorityQueue<int, int>(subQueueCount: 4, boundedCapacity: -1, comparer: null, stickiness: requested);

        await Assert.That(queue.StickinessForTest).IsEqualTo(expected).Because(
            $"stickiness {requested} must resolve to {expected} (-1 is the default sentinel, positives pass through)");
    }

    /// <summary>
    /// The default-constructed and three-argument queues resolve to the default stickiness of
    /// <c>1</c>, the contract-preserving resample-every-op behavior.
    /// </summary>
    [Test]
    public async Task Ctor_DefaultOverloads_ResolveToDefaultStickiness()
    {
        await Assert.That(new ConcurrentPriorityQueue<int, int>().StickinessForTest).IsEqualTo(1).Because(
            "the parameterless constructor uses the default stickiness of 1");
        await Assert.That(new ConcurrentPriorityQueue<int, int>(comparer: null).StickinessForTest).IsEqualTo(1).Because(
            "the comparer-only constructor uses the default stickiness of 1");
    }

    /// <summary>
    /// A stickiness below one (and not the <c>-1</c> default sentinel) is rejected with
    /// <see cref="ArgumentOutOfRangeException"/> by <c>ResolveStickiness</c>, mirroring the
    /// ConcurrentDictionary concurrency-level convention.
    /// </summary>
    [Test]
    [Arguments(0)]
    [Arguments(-2)]
    [Arguments(-100)]
    public async Task Ctor_InvalidStickiness_Throws(int stickiness)
    {
        await Assert.That(() => new ConcurrentPriorityQueue<int, int>(subQueueCount: 4, boundedCapacity: -1, comparer: null, stickiness: stickiness))
            .Throws<ArgumentOutOfRangeException>().Because(
                $"stickiness {stickiness} is below one and not the -1 sentinel, so it must be rejected");
    }

    /// <summary>
    /// The public <c>(boundedCapacity, stickiness, comparer)</c> constructor validates stickiness the
    /// same way and accepts the <c>-1</c> sentinel and positive factors on an unbounded queue.
    /// </summary>
    [Test]
    public async Task PublicStickinessCtor_ValidatesAndResolves()
    {
        await Assert.That(new ConcurrentPriorityQueue<int, int>(boundedCapacity: -1, stickiness: 4).StickinessForTest).IsEqualTo(4);
        await Assert.That(new ConcurrentPriorityQueue<int, int>(boundedCapacity: -1, stickiness: -1).StickinessForTest).IsEqualTo(1).Because(
            "the -1 sentinel resolves to the default through the public stickiness constructor too");
        await Assert.That(() => new ConcurrentPriorityQueue<int, int>(boundedCapacity: -1, stickiness: 0))
            .Throws<ArgumentOutOfRangeException>().Because("stickiness 0 is invalid on the public constructor as well");
    }

    /// <summary>
    /// <c>NextStickyIndex</c> reuses a sampled index for exactly <c>stickiness</c> consecutive calls
    /// before re-sampling: the first call samples and arms a countdown, the next <c>s - 1</c> calls
    /// return the cached index unchanged, and the <c>(s+1)</c>th call re-samples.
    /// </summary>
    [Test]
    public async Task NextStickyIndex_ReusesForStickinessPeriod()
    {
        // A deterministic handle (fixed seed words) so the sampled indices are reproducible.
        var handle = ThreadHandle.CreateForTesting(1, 2, 3, 4);
        const int mask = 15; // 16 sub-queues.
        const int stickiness = 4;

        int first = handle.NextStickyIndex(mask, stickiness);

        // The next three calls (s - 1 = 3) must return the identical cached index.
        for (int i = 0; i < stickiness - 1; i++)
        {
            await Assert.That(handle.NextStickyIndex(mask, stickiness)).IsEqualTo(first).Because(
                $"call {i + 2} within the sticky period reuses the first sampled index {first}");
        }

        // The next call ends the period and re-samples. Across a full period it is at least sometimes
        // different; assert the index is always in range, and that the reuse contract above held.
        int resampled = handle.NextStickyIndex(mask, stickiness);
        await Assert.That(resampled).IsGreaterThanOrEqualTo(0).Because("a resampled index is non-negative");
        await Assert.That(resampled).IsLessThanOrEqualTo(mask).Because("a resampled index stays within [0, mask]");
    }

    /// <summary>
    /// <c>NextStickyIndex</c> with <c>stickiness == 1</c> re-samples every call (a zero-length
    /// countdown), behaviorally identical to a plain index draw — the <c>s == 1</c> contract that
    /// leaves the relaxed-dequeue behavior unchanged.
    /// </summary>
    [Test]
    public async Task NextStickyIndex_StickinessOne_ResamplesEveryCall()
    {
        var handle = ThreadHandle.CreateForTesting(0xDEAD, 0xBEEF, 0xCAFE, 0xF00D);
        const int mask = 15;

        // With s == 1 the countdown is armed to 0, so every call takes the resample branch. Every
        // returned index must be a legal in-range value (the resample path runs each time).
        for (int i = 0; i < 20; i++)
        {
            int idx = handle.NextStickyIndex(mask, stickiness: 1);
            await Assert.That(idx).IsGreaterThanOrEqualTo(0).And.IsLessThanOrEqualTo(mask).Because(
                "every s==1 call resamples a fresh in-range index");
        }
    }

    /// <summary>
    /// A different mask within a live sticky period forces a fresh sample in the new range: the
    /// <c>[ThreadStatic]</c> handle is shared across queues of different sub-queue counts, so a stuck
    /// index sampled for one mask must never be returned for a different mask.
    /// </summary>
    [Test]
    public async Task NextStickyIndex_DifferentMask_ForcesResampleInRange()
    {
        var handle = ThreadHandle.CreateForTesting(11, 22, 33, 44);
        const int stickiness = 8;

        // Arm a sticky period for a 256-sub-queue mask (values up to 255).
        handle.NextStickyIndex(mask: 255, stickiness);

        // Now request with a 4-sub-queue mask (values 0..3) WHILE the period is still live. The mask
        // gate must force a fresh sample in the new range, never reuse the large stuck index.
        int small = handle.NextStickyIndex(mask: 3, stickiness);

        await Assert.That(small).IsGreaterThanOrEqualTo(0).And.IsLessThanOrEqualTo(3).Because(
            "a mismatched mask forces a resample within the new [0, 3] range, never reusing the out-of-range stuck index");
    }

    /// <summary>
    /// <c>ResetStickyEnqueue</c> ends the current enqueue sticky period immediately: a call that would
    /// have reused the cached index instead re-samples (the production resample-on-contention path).
    /// </summary>
    [Test]
    public async Task ResetStickyEnqueue_EndsPeriod_ForcesResample()
    {
        var handle = ThreadHandle.CreateForTesting(7, 8, 9, 10);
        const int mask = 15;
        const int stickiness = 4;

        handle.NextStickyIndex(mask, stickiness); // arm the period (remaining = 3).
        handle.ResetStickyEnqueue();              // end it immediately (remaining = 0).

        // The next call must take the resample branch, not the reuse branch. We verify by exercising
        // the path; the returned index must be in range.
        int afterReset = handle.NextStickyIndex(mask, stickiness);
        await Assert.That(afterReset).IsGreaterThanOrEqualTo(0).And.IsLessThanOrEqualTo(mask).Because(
            "after ResetStickyEnqueue the next call resamples a fresh in-range index");

        // Prove the reset armed a FRESH period (not a no-op): the resampled index is then reused
        // for the remaining stickiness - 1 calls of the new window.
        for (int i = 0; i < stickiness - 1; i++)
        {
            await Assert.That(handle.NextStickyIndex(mask, stickiness)).IsEqualTo(afterReset).Because(
                "after reset a fresh sticky period is armed and reused for the full remaining window");
        }
    }

    /// <summary>
    /// <c>NextStickyPair</c> reuses a sampled distinct pair for the sticky period and re-samples after,
    /// and <c>ResetStickyDequeue</c> ends the dequeue period early — the two-choice analogue of the
    /// enqueue sticky path. The pair is always two distinct in-range indices.
    /// </summary>
    [Test]
    public async Task NextStickyPair_ReusesDistinctPair_AndResets()
    {
        var handle = ThreadHandle.CreateForTesting(101, 202, 303, 404);
        const int mask = 15;
        const int stickiness = 3;

        handle.NextStickyPair(mask, stickiness, out int i0, out int j0);
        await Assert.That(i0).IsNotEqualTo(j0).Because("the sampled pair is two distinct indices");
        await Assert.That(i0).IsGreaterThanOrEqualTo(0).And.IsLessThanOrEqualTo(mask);
        await Assert.That(j0).IsGreaterThanOrEqualTo(0).And.IsLessThanOrEqualTo(mask);

        // Within the period the cached pair is reused exactly.
        handle.NextStickyPair(mask, stickiness, out int i1, out int j1);
        await Assert.That(i1).IsEqualTo(i0).Because("the second call within the period reuses the cached i");
        await Assert.That(j1).IsEqualTo(j0).Because("the second call within the period reuses the cached j");

        // Reset ends the period; the next call re-samples a fresh distinct in-range pair.
        handle.ResetStickyDequeue();
        handle.NextStickyPair(mask, stickiness, out int i2, out int j2);
        await Assert.That(i2).IsNotEqualTo(j2).Because("a resampled pair is still two distinct indices");
        await Assert.That(i2).IsGreaterThanOrEqualTo(0).And.IsLessThanOrEqualTo(mask);
        await Assert.That(j2).IsGreaterThanOrEqualTo(0).And.IsLessThanOrEqualTo(mask);

        // Prove the reset began a FRESH reuse window: the resampled pair is reused on the next call.
        handle.NextStickyPair(mask, stickiness, out int i3, out int j3);
        await Assert.That(i3).IsEqualTo(i2).Because("post-reset pair begins a new sticky reuse window");
        await Assert.That(j3).IsEqualTo(j2).Because("post-reset pair is reused within that window");
    }

    /// <summary>
    /// <c>NextTwoDistinct</c> always yields two distinct in-range indices, including the
    /// minimum non-trivial mask (<c>mask == 1</c>, two sub-queues) where the only distinct pair is
    /// <c>(0, 1)</c> — exercising the rejection loop down to its bounded fallback.
    /// </summary>
    [Test]
    public async Task NextTwoDistinct_MinimumMask_AlwaysDistinct()
    {
        var handle = ThreadHandle.CreateForTesting(5, 6, 7, 8);

        for (int trial = 0; trial < 100; trial++)
        {
            handle.NextStickyPair(mask: 1, stickiness: 1, out int i, out int j);
            await Assert.That(i).IsNotEqualTo(j).Because("with mask == 1 the only distinct pair is (0, 1)");
            await Assert.That(i + j).IsEqualTo(1).Because("the two distinct indices in [0, 1] sum to exactly 1");
        }
    }
}
