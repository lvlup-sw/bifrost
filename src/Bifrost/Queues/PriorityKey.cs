// =============================================================================
// <copyright file="PriorityKey.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Runtime.CompilerServices;

using Bifrost.Core;

namespace Bifrost.Queues;

/// <summary>
/// Computes the WFQ-style virtual-time priority key for class-based dispatch
/// (design DR-5): <c>effectivePriority = EnqueuedAtTicks − Boost(Class)</c>,
/// where smaller = sooner.
/// </summary>
/// <remarks>
/// <para>
/// <b>Virtual-time rationale.</b> Like weighted fair queueing, each item is ranked by
/// a virtual timestamp rather than a discrete priority level: the class boost shifts
/// the item's enqueue timestamp backward (Interactive) or forward (Batch penalty) in
/// virtual time, and the queue simply dispatches the smallest key first. Priorities
/// and arrival order therefore live on a single comparable axis — there are no
/// per-class queues to balance and no aging scans to run.
/// </para>
/// <para>
/// <b>Starvation bound, by construction.</b> Because the boost is a bounded constant,
/// aging is inherent: any item that has waited longer than
/// <see cref="PriorityDispatchOptions.InteractiveBoostWindow"/> has a smaller key than
/// a freshly enqueued Interactive item and dispatches first. The boost window IS the
/// starvation bound — no decrease-key, no re-scoring (DR-5).
/// </para>
/// <para>
/// <b>Relaxed dispatch noise.</b> The lock-free MultiQueue binding dequeues with a
/// documented expected rank error of <c>(5/6)·n</c>; that relaxation rides on top of
/// the total order this key defines and is acceptable dispatch-ordering noise (DR-5).
/// The key guarantees the ordering intent; bindings may realize it approximately.
/// </para>
/// <para>
/// <b>Units.</b> <see cref="WorkEnvelope{TWork}.EnqueuedAtTicks"/> is in
/// <see cref="TimeProvider.TimestampFrequency"/> units — not necessarily
/// <see cref="TimeSpan"/> ticks — so boost windows must be converted at that same
/// frequency. <see cref="Precompute"/> performs the floating-point conversion exactly
/// once per options + frequency pair; <see cref="Compute{TWork}"/> is pure
/// <see cref="long"/> arithmetic on the hot path.
/// </para>
/// <para>
/// <b>Overflow.</b> <c>timestamp − boost</c> cannot meaningfully overflow within a
/// process lifetime at realistic frequencies: even at 1 GHz, <see cref="long"/> spans
/// roughly 292 years of uptime, and boost windows are seconds to minutes. Keys may be
/// negative early in process life (boost larger than the young timestamp) — keys are
/// ordinal only, and negative values order correctly.
/// </para>
/// </remarks>
public static class PriorityKey
{
    /// <summary>
    /// Converts the configured boost windows into timestamp-frequency units, once per
    /// options + frequency pair, so the per-item hot path never touches floating point.
    /// </summary>
    /// <param name="options">The priority dispatch options to convert.</param>
    /// <param name="timestampFrequency">
    /// The timestamp frequency, in units per second, of the <see cref="TimeProvider"/>
    /// that stamps <see cref="WorkEnvelope{TWork}.EnqueuedAtTicks"/> — i.e. its
    /// <see cref="TimeProvider.TimestampFrequency"/>.
    /// </param>
    /// <returns>The precomputed per-class boosts in timestamp units.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="options"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="timestampFrequency"/> is zero or negative.
    /// </exception>
    public static Boosts Precompute(PriorityDispatchOptions options, long timestampFrequency)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);

        var interactiveBoostTicks =
            (long)(options.InteractiveBoostWindow.TotalSeconds * timestampFrequency);
        var batchPenaltyTicks =
            (long)(options.BatchPenaltyWindow.TotalSeconds * timestampFrequency);

        // Boost(Batch) = −BatchPenaltyWindow: subtracting a negative boost ADDS the
        // penalty, deferring batch work in virtual time.
        return new Boosts(interactiveBoostTicks, -batchPenaltyTicks);
    }

    /// <summary>
    /// Computes the effective priority key for an enveloped work item. Smaller = sooner.
    /// </summary>
    /// <typeparam name="TWork">The type of work item in the envelope.</typeparam>
    /// <param name="envelope">The enveloped work item to rank.</param>
    /// <param name="boosts">
    /// The boosts precomputed via <see cref="Precompute"/> for the same
    /// <see cref="TimeProvider.TimestampFrequency"/> that stamped the envelope.
    /// </param>
    /// <returns>
    /// <c>EnqueuedAtTicks − Boost(Class)</c> in timestamp units. Pure
    /// <see cref="long"/> arithmetic — no floating point, no allocation.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Compute<TWork>(in WorkEnvelope<TWork> envelope, in Boosts boosts)
        => envelope.EnqueuedAtTicks - boosts.BoostTicksFor(envelope.Class);

    /// <summary>
    /// Per-class boost values precomputed in timestamp-frequency units. Obtain via
    /// <see cref="Precompute"/>; reuse for every <see cref="Compute{TWork}"/> call
    /// against the same options + frequency pair.
    /// </summary>
    /// <remarks>
    /// The <c>default</c> instance has zero boost for every class, which degrades
    /// priority dispatch to plain FIFO by enqueue timestamp — harmless, but almost
    /// certainly not what a priority binding wants; always go through
    /// <see cref="Precompute"/>.
    /// </remarks>
    public readonly struct Boosts
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Boosts"/> struct.
        /// </summary>
        /// <param name="interactiveBoostTicks">
        /// The Interactive boost in timestamp units (non-negative).
        /// </param>
        /// <param name="batchBoostTicks">
        /// The Batch boost in timestamp units (zero or negative: a configured penalty
        /// window is stored as a negative boost).
        /// </param>
        internal Boosts(long interactiveBoostTicks, long batchBoostTicks)
        {
            InteractiveBoostTicks = interactiveBoostTicks;
            BatchBoostTicks = batchBoostTicks;
        }

        /// <summary>
        /// Gets the boost, in timestamp units, subtracted from an Interactive item's
        /// enqueue timestamp.
        /// </summary>
        public long InteractiveBoostTicks { get; }

        /// <summary>
        /// Gets the boost, in timestamp units, subtracted from a Batch item's enqueue
        /// timestamp. Zero by default; negative when a penalty window is configured.
        /// </summary>
        public long BatchBoostTicks { get; }

        /// <summary>
        /// Gets the boost, in timestamp units, for the given work class.
        /// <see cref="WorkClass.Default"/> (and any unrecognized value) has zero boost.
        /// </summary>
        /// <param name="workClass">The work class to look up.</param>
        /// <returns>The boost in timestamp units; subtract it from the timestamp.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long BoostTicksFor(WorkClass workClass)
            => workClass switch
            {
                WorkClass.Interactive => InteractiveBoostTicks,
                WorkClass.Batch => BatchBoostTicks,
                _ => 0L,
            };
    }
}
