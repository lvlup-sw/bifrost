// =============================================================================
// <copyright file="MissedFirePolicyApplier.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;

namespace Bifrost.Scheduling.Internal;

/// <summary>
/// Reconciles the occurrences a job missed while it could not fire (DR-3): it
/// enumerates the cadence's occurrences strictly after the last fire and up to
/// <c>now</c>, then projects that backlog down to the catch-up instants the
/// configured <see cref="MissedFirePolicy"/> requires.
/// </summary>
/// <remarks>
/// Every walk of a cadence visits at most the catch-up cap of occurrences and observes its
/// cancellation token before each step, so a dense cadence and a long gap cannot hold the
/// caller inside the walk.
/// </remarks>
internal static class MissedFirePolicyApplier
{
    /// <summary>
    /// Computes the catch-up fire instants for a job's missed-fire backlog under the
    /// supplied <paramref name="policy"/>.
    /// </summary>
    /// <param name="cadence">The cadence whose occurrences are enumerated.</param>
    /// <param name="lastFiredAt">
    /// The instant the job most recently fired, or <see langword="null"/> if it has
    /// never fired. Enumeration considers only occurrences strictly after this.
    /// </param>
    /// <param name="now">The current instant (DR-7); the inclusive upper bound.</param>
    /// <param name="policy">How the missed backlog is reconciled.</param>
    /// <param name="maxCatchUpCount">
    /// The maximum number of occurrences a walk of the cadence visits: the backlog enumerated
    /// for <see cref="MissedFirePolicy.FireAllMissed"/>, and the walk
    /// <see cref="MissedFirePolicy.Coalesce"/> falls back to for a cadence without a
    /// constant-time answer.
    /// </param>
    /// <param name="cancellationToken">Observed before each step of a walk.</param>
    /// <returns>
    /// The catch-up instants, oldest first: a single instant for
    /// <see cref="MissedFirePolicy.Coalesce"/>, the full (capped) backlog for
    /// <see cref="MissedFirePolicy.FireAllMissed"/>, and an empty list for
    /// <see cref="MissedFirePolicy.SkipMissed"/> or when nothing was missed.
    /// </returns>
    /// <remarks>
    /// <see cref="MissedFirePolicy.SkipMissed"/> returns before the walk starts, so it discards the
    /// whole backlog without enumerating it.
    /// </remarks>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled during a walk.
    /// </exception>
    internal static IReadOnlyList<DateTimeOffset> ComputeMissedFires(
        Cadence cadence,
        DateTimeOffset? lastFiredAt,
        DateTimeOffset now,
        MissedFirePolicy policy,
        int maxCatchUpCount = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cadence);

        if (policy == MissedFirePolicy.SkipMissed)
        {
            return [];
        }

        if (policy == MissedFirePolicy.Coalesce && TryCoalesceInConstantTime(cadence, lastFiredAt, now, out var coalesced))
        {
            return coalesced;
        }

        var missed = EnumerateMissed(cadence, lastFiredAt, now, maxCatchUpCount, cancellationToken);
        return policy == MissedFirePolicy.Coalesce ? Latest(missed) : missed;
    }

    /// <summary>
    /// Computes, from one walk of the cadence, both the catch-up instants for
    /// <paramref name="policy"/> and the number of missed occurrences (capped at
    /// <paramref name="maxCatchUpCount"/>).
    /// </summary>
    /// <param name="cadence">The cadence whose occurrences are enumerated.</param>
    /// <param name="lastFiredAt">The last fire instant, or <see langword="null"/>.</param>
    /// <param name="now">The current instant (DR-7); the inclusive upper bound.</param>
    /// <param name="policy">How the missed backlog is reconciled.</param>
    /// <param name="maxCatchUpCount">The maximum number of occurrences the walk visits.</param>
    /// <param name="cancellationToken">Observed before each step of the walk.</param>
    /// <returns>
    /// The catch-up instants, as <see cref="ComputeMissedFires"/> returns them, and the missed
    /// count that <see cref="MissedFirePolicy.FireAllMissed"/> would enumerate.
    /// </returns>
    /// <remarks>
    /// <para>
    /// For an interval cadence, the missed count is the number of aligned occurrences after the last
    /// fire, capped at <paramref name="maxCatchUpCount"/>. This is the same arithmetic that selects
    /// the coalesced catch-up, so jitter on the next live instant cannot report zero missed fires
    /// while an aligned catch-up is due. Only <see cref="MissedFirePolicy.FireAllMissed"/> walks an
    /// interval cadence, to list its backlog.
    /// </para>
    /// <para>
    /// For any other cadence, the missed count is the backlog the
    /// <see cref="MissedFirePolicy.FireAllMissed"/> walk yields, so a coalesce without a constant-time
    /// answer takes the latest occurrence of that same walk rather than walking the cadence a second
    /// time.
    /// </para>
    /// </remarks>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled during the walk.
    /// </exception>
    internal static (IReadOnlyList<DateTimeOffset> CatchUp, int MissedCount) ComputeBacklog(
        Cadence cadence,
        DateTimeOffset? lastFiredAt,
        DateTimeOffset now,
        MissedFirePolicy policy,
        int maxCatchUpCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cadence);

        if (cadence is IntervalCadence interval)
        {
            var latest = LatestAlignedOccurrence(interval, lastFiredAt, now, out var aligned);
            IReadOnlyList<DateTimeOffset> intervalCatchUp = policy switch
            {
                MissedFirePolicy.SkipMissed => [],
                MissedFirePolicy.Coalesce => latest is { } occurrence ? [occurrence] : [],
                _ => EnumerateMissed(cadence, lastFiredAt, now, maxCatchUpCount, cancellationToken),
            };

            return (intervalCatchUp, (int)Math.Min(aligned, maxCatchUpCount));
        }

        var missed = EnumerateMissed(cadence, lastFiredAt, now, maxCatchUpCount, cancellationToken);
        IReadOnlyList<DateTimeOffset> catchUp = policy switch
        {
            MissedFirePolicy.SkipMissed => [],
            MissedFirePolicy.Coalesce => TryCoalesceInConstantTime(cadence, lastFiredAt, now, out var coalesced)
                ? coalesced
                : Latest(missed),
            _ => missed,
        };

        return (catchUp, missed.Count);
    }

    /// <summary>
    /// Computes the coalesced catch-up instant without walking the cadence, when the cadence
    /// supports it: the latest occurrence strictly after <paramref name="lastFiredAt"/> and at
    /// or before <paramref name="now"/>.
    /// </summary>
    /// <param name="cadence">The cadence whose occurrences are coalesced.</param>
    /// <param name="lastFiredAt">The last fire instant, or <see langword="null"/>.</param>
    /// <param name="now">The inclusive upper bound.</param>
    /// <param name="coalesced">The coalesced instant, or an empty list when nothing was missed.</param>
    /// <returns>
    /// <see langword="true"/> for an interval or cron cadence; <see langword="false"/> when the
    /// caller must take the latest occurrence of a capped walk instead.
    /// </returns>
    /// <remarks>
    /// A jittered interval uses the same arithmetic as an un-jittered one, so its catch-up is the
    /// latest aligned occurrence: jitter only spreads live fires and has no stable missed instant
    /// to replay.
    /// </remarks>
    private static bool TryCoalesceInConstantTime(
        Cadence cadence,
        DateTimeOffset? lastFiredAt,
        DateTimeOffset now,
        out IReadOnlyList<DateTimeOffset> coalesced)
    {
        if (cadence is IntervalCadence interval)
        {
            coalesced = LatestAlignedOccurrence(interval, lastFiredAt, now, out _) is { } latest ? [latest] : [];
            return true;
        }

        if (cadence is CronCadence cron)
        {
            if (lastFiredAt is not { } last || last >= now)
            {
                coalesced = [];
                return true;
            }

            var previous = cron.ComputePreviousFire(now);
            coalesced = previous is { } occurrence && occurrence > last ? [occurrence] : [];
            return true;
        }

        coalesced = [];
        return false;
    }

    /// <summary>
    /// Counts the aligned interval occurrences strictly after <paramref name="lastFiredAt"/> and at
    /// or before <paramref name="now"/>, without walking the cadence, and returns the latest one.
    /// </summary>
    /// <param name="interval">The interval cadence; its jitter is ignored.</param>
    /// <param name="lastFiredAt">The last fire instant, or <see langword="null"/>.</param>
    /// <param name="now">The inclusive upper bound.</param>
    /// <param name="count">The number of aligned occurrences, or zero when nothing was missed.</param>
    /// <returns>The latest aligned occurrence, or <see langword="null"/> when nothing was missed.</returns>
    private static DateTimeOffset? LatestAlignedOccurrence(
        IntervalCadence interval,
        DateTimeOffset? lastFiredAt,
        DateTimeOffset now,
        out long count)
    {
        if (lastFiredAt is not { } last || last >= now)
        {
            count = 0;
            return null;
        }

        var remaining = Math.Min(
            DateTimeOffset.MaxValue.Ticks - last.Ticks,
            DateTimeOffset.MaxValue.UtcTicks - last.UtcTicks);
        count = Math.Min(
            (now - last).Ticks / interval.Interval.Ticks,
            remaining / interval.Interval.Ticks);
        return count == 0 ? null : last.AddTicks(count * interval.Interval.Ticks);
    }

    private static IReadOnlyList<DateTimeOffset> Latest(List<DateTimeOffset> missed)
        => missed.Count == 0 ? [] : [missed[^1]];

    /// <summary>
    /// Enumerates the occurrences strictly after <paramref name="lastFiredAt"/> and at
    /// or before <paramref name="now"/>, walking the cadence one occurrence at a time
    /// and stopping at the cap. The cursor passed to
    /// <see cref="Cadence.ComputeNextFire"/> is the previous occurrence, so each call
    /// yields the next occurrence strictly after it.
    /// </summary>
    /// <remarks>
    /// The clock argument is anchored at the cursor, so <see cref="Cadence.ComputeNextFire"/> yields
    /// the next occurrence strictly after the previous one. With the real <c>now</c>, an interval
    /// cadence would skip past <c>now</c>. The walk stops when there is no further occurrence (for
    /// example a one-shot that already fired), when the next occurrence is in the future (the backlog
    /// is exhausted), or when the cadence does not advance (a guard against an infinite loop).
    /// </remarks>
    /// <param name="cadence">The cadence to enumerate.</param>
    /// <param name="lastFiredAt">The last fire instant, or <see langword="null"/>.</param>
    /// <param name="now">The inclusive upper bound.</param>
    /// <param name="maxCatchUpCount">The maximum occurrences to enumerate.</param>
    /// <param name="cancellationToken">Observed before each step of the walk.</param>
    /// <returns>The missed occurrences in chronological order.</returns>
    private static List<DateTimeOffset> EnumerateMissed(
        Cadence cadence,
        DateTimeOffset? lastFiredAt,
        DateTimeOffset now,
        int maxCatchUpCount,
        CancellationToken cancellationToken)
    {
        var result = new List<DateTimeOffset>();
        var cursor = lastFiredAt;

        while (result.Count < maxCatchUpCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clock = cursor ?? now;
            var next = cadence.ComputeNextFire(cursor, clock);

            if (next is null)
            {
                break;
            }

            if (next.Value > now)
            {
                break;
            }

            if (cursor is not null && next.Value <= cursor.Value)
            {
                break;
            }

            result.Add(next.Value);
            cursor = next.Value;
        }

        return result;
    }
}
