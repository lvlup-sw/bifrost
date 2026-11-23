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
    /// The maximum number of occurrences enumerated: the backlog cap for
    /// <see cref="MissedFirePolicy.FireAllMissed"/>, and the step cap of the
    /// <see cref="MissedFirePolicy.Coalesce"/> walk for a cadence with no constant-time path.
    /// </param>
    /// <param name="cancellationToken">Stops the enumeration between occurrences.</param>
    /// <returns>
    /// The catch-up instants, oldest first: a single instant for
    /// <see cref="MissedFirePolicy.Coalesce"/>, the full (capped) backlog for
    /// <see cref="MissedFirePolicy.FireAllMissed"/>, and an empty list for
    /// <see cref="MissedFirePolicy.SkipMissed"/> or when nothing was missed.
    /// </returns>
    /// <remarks>
    /// <see cref="MissedFirePolicy.SkipMissed"/> returns before the walk starts, so it discards the
    /// whole backlog without enumerating it. No policy calls <see cref="Cadence.ComputeNextFire"/>
    /// more than <paramref name="maxCatchUpCount"/> times, so a short interval or a cadence that
    /// advances one tick at a time cannot hold the caller for the length of the downtime.
    /// </remarks>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled during the enumeration.
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

        if (policy == MissedFirePolicy.Coalesce)
        {
            return Coalesce(cadence, lastFiredAt, now, maxCatchUpCount, cancellationToken);
        }

        var missed = EnumerateMissed(cadence, lastFiredAt, now, maxCatchUpCount, cancellationToken);

        if (missed.Count == 0)
        {
            return [];
        }

        return missed;
    }

    /// <summary>
    /// Selects the latest missed occurrence.
    /// </summary>
    /// <remarks>
    /// Interval and cron cadences are resolved in constant time. A jittered interval takes the
    /// latest aligned occurrence and applies its jitter once, through
    /// <see cref="Cadence.ComputeNextFire"/> anchored one interval earlier. A jittered instant that
    /// lands after <c>now</c> falls back to the aligned occurrence. Any other cadence is walked at
    /// most <paramref name="maxCatchUpCount"/> steps; a walk that reaches the cap without catching
    /// up to <c>now</c> is treated as exhausted and coalesces to the last occurrence it reached.
    /// </remarks>
    private static IReadOnlyList<DateTimeOffset> Coalesce(
        Cadence cadence,
        DateTimeOffset? lastFiredAt,
        DateTimeOffset now,
        int maxCatchUpCount,
        CancellationToken cancellationToken)
    {
        if (cadence is IntervalCadence interval)
        {
            if (LatestAlignedOccurrence(interval, lastFiredAt, now) is not { } aligned)
            {
                return [];
            }

            if (interval.Jitter == 0)
            {
                return [aligned];
            }

            var previous = aligned.AddTicks(-interval.Interval.Ticks);
            var jittered = interval.ComputeNextFire(previous, previous);
            return jittered is { } fire && fire <= now ? [fire] : [aligned];
        }

        if (cadence is CronCadence cron)
        {
            if (lastFiredAt is not { } last || last >= now)
            {
                return [];
            }

            var previous = cron.ComputePreviousFire(now);
            return previous is { } occurrence && occurrence > last ? [occurrence] : [];
        }

        DateTimeOffset? latest = null;
        var cursor = lastFiredAt;
        for (var step = 0; step < maxCatchUpCount; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = cadence.ComputeNextFire(cursor, cursor ?? now);
            if (next is null || next > now || (cursor is not null && next <= cursor))
            {
                break;
            }

            latest = next;
            cursor = next;
        }

        return latest is { } reached ? [reached] : [];
    }

    /// <summary>
    /// Returns the latest whole-interval step from <paramref name="lastFiredAt"/> that is at or
    /// before <paramref name="now"/>, or <see langword="null"/> when no step was missed.
    /// </summary>
    /// <param name="interval">The interval cadence.</param>
    /// <param name="lastFiredAt">The last fire instant, or <see langword="null"/>.</param>
    /// <param name="now">The inclusive upper bound.</param>
    /// <returns>The latest aligned occurrence, or <see langword="null"/>.</returns>
    /// <remarks>
    /// The step count is limited so that both the local and the UTC date stay representable.
    /// </remarks>
    private static DateTimeOffset? LatestAlignedOccurrence(
        IntervalCadence interval,
        DateTimeOffset? lastFiredAt,
        DateTimeOffset now)
    {
        if (lastFiredAt is not { } last || last >= now)
        {
            return null;
        }

        var remaining = Math.Min(
            DateTimeOffset.MaxValue.Ticks - last.Ticks,
            DateTimeOffset.MaxValue.UtcTicks - last.UtcTicks);
        var count = Math.Min(
            (now - last).Ticks / interval.Interval.Ticks,
            remaining / interval.Interval.Ticks);
        return count == 0 ? null : last.AddTicks(count * interval.Interval.Ticks);
    }

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
    /// <param name="cancellationToken">Stops the enumeration between occurrences.</param>
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
