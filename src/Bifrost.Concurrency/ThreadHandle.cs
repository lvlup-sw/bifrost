// =============================================================================
// <copyright file="ThreadHandle.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Numerics;
using System.Runtime.CompilerServices;

namespace Bifrost.Concurrency;

/// <summary>
/// Per-thread state object used by the MultiQueue priority queue for random
/// sub-queue selection. Carries an inline <c>xoshiro256**</c> random number
/// generator (Blackman/Vigna) and two-choice index helpers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread-affinity contract:</b> A handle is bound to the thread that created it
/// via <see cref="Current"/>. Correctness must <i>never</i> depend on thread identity:
/// the handle is only a private source of fast, uncontended randomness for picking
/// sub-queues, never a key into shared state. Callers MUST re-fetch <see cref="Current"/>
/// per operation and MUST NOT cache a handle across an <c>await</c> boundary. Because the
/// continuation after an <c>await</c> may resume on a different thread-pool thread,
/// a cached handle could be shared concurrently by two threads, which would corrupt the
/// RNG state. Re-fetching per operation keeps each in-flight call bound to the thread it
/// actually runs on and is safe under thread-pool churn.
/// </para>
/// <para>
/// <b>Stickiness state is a hint:</b> the handle also carries a small stuck-selection +
/// countdown (<see cref="NextStickyIndex"/>, <see cref="NextStickyPair"/>) so a thread can
/// reuse a sampled sub-queue for <c>s</c> consecutive operations (ESA 2021), amortizing the
/// sampling cost. Like the RNG, this state is <i>only</i> a private bias on which sub-queue is
/// sampled, never a key into shared state, so a stale countdown inherited across thread-pool
/// churn is harmless (at worst one slightly-worse sample), needs no synchronization, and never
/// affects correctness.
/// </para>
/// <para>
/// <b>Non-generic by design:</b> This class is intentionally non-generic so that the
/// <c>[ThreadStatic]</c> handle is shared across every generic instantiation of the
/// future queue, rather than allocating one handle per closed generic type per thread.
/// </para>
/// <para>
/// <b>Seeding policy:</b> Default handles are seeded from <see cref="Random.Shared"/>.
/// xoshiro256** forbids the all-zero state (it is a fixed point that produces only
/// zeros), so seeding re-draws until at least one of the four 64-bit state words is
/// non-zero.
/// </para>
/// </remarks>
internal sealed class ThreadHandle
{
    [ThreadStatic]
    private static ThreadHandle? t_current;

    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;

    // Stickiness state (ESA 2021): reuse a sampled sub-queue selection for s consecutive
    // operations before re-sampling, amortizing the per-op sampling cost. These are HINTS
    // only: like the RNG, stale values are harmless (they only ever bias which sub-queue is
    // sampled, never key into shared state), so they need no synchronization and survive
    // thread-pool churn. The enqueue side sticks a single index; the dequeue side sticks the
    // sampled pair. A countdown of 0 means "re-sample on the next call", so s == 1 reproduces
    // the original resample-every-op behavior exactly.
    //
    // The mask the selection was sampled for is stored alongside it: the [ThreadStatic] handle is
    // shared across EVERY queue instance on the thread, and those queues may have different sub-queue
    // counts. A stuck index sampled for a 16-sub-queue queue is out of range for a 4-sub-queue one, so
    // reuse is gated on the mask matching: a different mask forces a fresh sample in the new range.
    // This is what keeps "stale stuck-state is harmless" literally true across heterogeneous queues.
    // Two queues with the SAME sub-queue count on one thread do share these fields, so their sticky
    // periods interleave when a thread alternates between them, still only a sampling bias
    // (correctness-neutral), it just dilutes the per-queue locality payoff in that pattern.
    private int _stuckEnqIndex;
    private int _stuckEnqMask;
    private int _stuckEnqRemaining;
    private int _stuckDeqI;
    private int _stuckDeqJ;
    private int _stuckDeqMask;
    private int _stuckDeqRemaining;

    /// <summary>
    /// Initializes a new <see cref="ThreadHandle"/> with state seeded from
    /// <see cref="Random.Shared"/>, guaranteeing a non-zero (legal) xoshiro256** state.
    /// </summary>
    private ThreadHandle()
    {
        // xoshiro256** is undefined for the all-zero state; re-draw until at least one
        // word is non-zero. The probability of an all-zero draw is negligible (2^-256),
        // so this loop effectively never iterates more than once.
        do
        {
            _s0 = unchecked((ulong)Random.Shared.NextInt64());
            _s1 = unchecked((ulong)Random.Shared.NextInt64());
            _s2 = unchecked((ulong)Random.Shared.NextInt64());
            _s3 = unchecked((ulong)Random.Shared.NextInt64());
        }
        while ((_s0 | _s1 | _s2 | _s3) == 0UL);
    }

    /// <summary>
    /// Initializes a new <see cref="ThreadHandle"/> with explicit state words.
    /// Used only by tests to exercise known-seed reference vectors.
    /// </summary>
    private ThreadHandle(ulong s0, ulong s1, ulong s2, ulong s3)
    {
        _s0 = s0;
        _s1 = s1;
        _s2 = s2;
        _s3 = s3;
    }

    /// <summary>
    /// Gets the calling thread's handle, lazily creating one on first access.
    /// </summary>
    /// <remarks>
    /// Callers must re-fetch per operation and must not cache the result across an
    /// <c>await</c>. See the type-level remarks for the thread-affinity contract.
    /// </remarks>
    internal static ThreadHandle Current => t_current ??= new ThreadHandle();

    /// <summary>
    /// Creates a handle with explicit xoshiro256** state for deterministic testing.
    /// </summary>
    /// <param name="s0">First state word.</param>
    /// <param name="s1">Second state word.</param>
    /// <param name="s2">Third state word.</param>
    /// <param name="s3">Fourth state word.</param>
    /// <returns>A handle seeded with the supplied state.</returns>
    internal static ThreadHandle CreateForTesting(ulong s0, ulong s1, ulong s2, ulong s3)
        => new ThreadHandle(s0, s1, s2, s3);

    /// <summary>
    /// Advances the xoshiro256** generator and returns the next 64-bit output.
    /// </summary>
    /// <returns>The next pseudo-random 64-bit value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ulong NextUInt64()
    {
        // xoshiro256** (Blackman/Vigna): scrambler then linear state transition.
        ulong result = BitOperations.RotateLeft(unchecked(_s1 * 5UL), 7) * 9UL;

        ulong t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = BitOperations.RotateLeft(_s3, 45);

        return result;
    }

    /// <summary>
    /// Returns a random index in the range <c>[0, mask]</c> using a bitmask.
    /// </summary>
    /// <param name="mask">A power-of-two-minus-one mask (<c>2^k - 1</c>) by construction.</param>
    /// <returns>A random index in <c>[0, mask]</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int NextIndex(int mask) => (int)(NextUInt64() & (ulong)mask);

    /// <summary>
    /// Draws two distinct random indices in the range <c>[0, mask]</c>.
    /// </summary>
    /// <param name="mask">A power-of-two-minus-one mask (<c>2^k - 1</c>) with <c>mask &gt;= 1</c>.</param>
    /// <param name="i">The first index.</param>
    /// <param name="j">The second index, distinct from <paramref name="i"/>.</param>
    /// <remarks>
    /// With <c>mask &gt;= 1</c> the rejection loop terminates with an expected fewer than two
    /// draws. A bounded fallback (<c>j = (i + 1) &amp; mask</c>) after eight attempts makes the
    /// method provably terminating regardless of the RNG stream.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void NextTwoDistinct(int mask, out int i, out int j)
    {
        i = NextIndex(mask);
        j = NextIndex(mask);

        int attempts = 0;
        while (j == i)
        {
            if (++attempts >= 8)
            {
                // Bounded fallback: guaranteed distinct when mask >= 1.
                j = (i + 1) & mask;
                return;
            }

            j = NextIndex(mask);
        }
    }

    /// <summary>
    /// Returns a sub-queue index using <i>stickiness</i>: the same sampled index is reused for
    /// <paramref name="stickiness"/> consecutive calls before a fresh index is drawn. The first
    /// call of a period samples via <see cref="NextIndex(int)"/> and arms a countdown; subsequent
    /// calls in the period return the cached index without consuming the RNG.
    /// </summary>
    /// <param name="mask">A power-of-two-minus-one mask (<c>2^k - 1</c>) by construction.</param>
    /// <param name="stickiness">
    /// The sticky period length <c>s &gt;= 1</c>. <c>s == 1</c> arms a zero-length countdown, so
    /// every call re-samples, behaviorally identical to <see cref="NextIndex(int)"/>; the residual
    /// hot-path cost is this method's predicted branch plus the period-refresh field stores.
    /// </param>
    /// <returns>The (possibly reused) sticky index in <c>[0, mask]</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int NextStickyIndex(int mask, int stickiness)
    {
        // Reuse only while the period is live AND the cached index was sampled for THIS mask: a
        // different mask (a different-sized queue on the same thread) must re-sample in its own range.
        if (_stuckEnqRemaining > 0 && _stuckEnqMask == mask)
        {
            _stuckEnqRemaining--;
            return _stuckEnqIndex;
        }

        _stuckEnqIndex = NextIndex(mask);
        _stuckEnqMask = mask;
        _stuckEnqRemaining = stickiness - 1;
        return _stuckEnqIndex;
    }

    /// <summary>
    /// Draws two distinct sub-queue indices using <i>stickiness</i>: the same sampled pair is
    /// reused for <paramref name="stickiness"/> consecutive calls before a fresh pair is drawn.
    /// The first call of a period samples via <see cref="NextTwoDistinct(int, out int, out int)"/>
    /// and arms a countdown; subsequent calls return the cached pair without consuming the RNG.
    /// </summary>
    /// <param name="mask">A power-of-two-minus-one mask (<c>2^k - 1</c>) with <c>mask &gt;= 1</c>.</param>
    /// <param name="stickiness">
    /// The sticky period length <c>s &gt;= 1</c>. <c>s == 1</c> re-samples every call, behaviorally
    /// identical to <see cref="NextTwoDistinct(int, out int, out int)"/>; the residual hot-path cost
    /// is this method's predicted branch plus the period-refresh field stores.
    /// </param>
    /// <param name="i">The first index.</param>
    /// <param name="j">The second index, distinct from <paramref name="i"/>.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void NextStickyPair(int mask, int stickiness, out int i, out int j)
    {
        // Reuse only while the period is live AND the cached pair was sampled for THIS mask: a
        // different mask (a different-sized queue on the same thread) must re-sample its own pair.
        if (_stuckDeqRemaining > 0 && _stuckDeqMask == mask)
        {
            _stuckDeqRemaining--;
            i = _stuckDeqI;
            j = _stuckDeqJ;
            return;
        }

        NextTwoDistinct(mask, out i, out j);
        _stuckDeqI = i;
        _stuckDeqJ = j;
        _stuckDeqMask = mask;
        _stuckDeqRemaining = stickiness - 1;
    }

    /// <summary>
    /// Ends the current enqueue sticky period immediately, so the next <see cref="NextStickyIndex"/>
    /// re-samples a fresh index. Called when the stuck sub-queue is contended (the wait-free-locking
    /// invariant forbids waiting on it) or observed empty: a fresh re-sample both preserves progress
    /// and starts a new period on an uncontended selection.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ResetStickyEnqueue() => _stuckEnqRemaining = 0;

    /// <summary>
    /// Ends the current dequeue sticky period immediately, so the next <see cref="NextStickyPair"/>
    /// re-samples a fresh pair. Called when the stuck pair is contended or drained, mirroring
    /// <see cref="ResetStickyEnqueue"/> on the two-choice side.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ResetStickyDequeue() => _stuckDeqRemaining = 0;
}
