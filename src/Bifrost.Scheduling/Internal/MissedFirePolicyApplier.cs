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
    /// The maximum number of missed occurrences enumerated. This bounds both the
    /// returned <see cref="MissedFirePolicy.FireAllMissed"/> backlog and the work the
    /// enumeration performs, so a long downtime never produces an unbounded list.
    /// </param>
    /// <returns>
    /// The catch-up instants, oldest first: a single instant for
    /// <see cref="MissedFirePolicy.Coalesce"/>, the full (capped) backlog for
    /// <see cref="MissedFirePolicy.FireAllMissed"/>, and an empty list for
    /// <see cref="MissedFirePolicy.SkipMissed"/> or when nothing was missed.
    /// </returns>
    internal static IReadOnlyList<DateTimeOffset> ComputeMissedFires(
        Cadence cadence,
        DateTimeOffset? lastFiredAt,
        DateTimeOffset now,
        MissedFirePolicy policy,
        int maxCatchUpCount = 100)
    {
        ArgumentNullException.ThrowIfNull(cadence);

        // SkipMissed discards the whole backlog without enumerating it.
        if (policy == MissedFirePolicy.SkipMissed)
        {
            return [];
        }

        var missed = EnumerateMissed(cadence, lastFiredAt, now, maxCatchUpCount);

        if (missed.Count == 0)
        {
            return [];
        }

        // Coalesce collapses the backlog into the single most recent catch-up.
        if (policy == MissedFirePolicy.Coalesce)
        {
            return [missed[^1]];
        }

        return missed;
    }

    /// <summary>
    /// Enumerates the occurrences strictly after <paramref name="lastFiredAt"/> and at
    /// or before <paramref name="now"/>, walking the cadence one occurrence at a time
    /// and stopping at the cap. The cursor passed to
    /// <see cref="Cadence.ComputeNextFire"/> is the previous occurrence, so each call
    /// yields the next occurrence strictly after it.
    /// </summary>
    /// <param name="cadence">The cadence to enumerate.</param>
    /// <param name="lastFiredAt">The last fire instant, or <see langword="null"/>.</param>
    /// <param name="now">The inclusive upper bound.</param>
    /// <param name="maxCatchUpCount">The maximum occurrences to enumerate.</param>
    /// <returns>The missed occurrences in chronological order.</returns>
    private static List<DateTimeOffset> EnumerateMissed(
        Cadence cadence,
        DateTimeOffset? lastFiredAt,
        DateTimeOffset now,
        int maxCatchUpCount)
    {
        var result = new List<DateTimeOffset>();
        var cursor = lastFiredAt;

        while (result.Count < maxCatchUpCount)
        {
            // Anchor the clock argument at the cursor so ComputeNextFire yields the
            // next occurrence strictly after the previous one rather than skipping
            // past now (which is what passing the real now would do for intervals).
            var clock = cursor ?? now;
            var next = cadence.ComputeNextFire(cursor, clock);

            // No further occurrence (for example a one-shot that already fired).
            if (next is null)
            {
                break;
            }

            // The next occurrence is in the future: the backlog is exhausted.
            if (next.Value > now)
            {
                break;
            }

            // Guard against a non-advancing cadence to avoid an infinite loop.
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
