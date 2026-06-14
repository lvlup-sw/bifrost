// =============================================================================
// <copyright file="Cadence.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Core;

/// <summary>
/// The abstract schedule that determines when a job fires.
/// </summary>
/// <remarks>
/// This is the polymorphic base carried by <see cref="JobRecord"/> and the
/// scheduling registry. Concrete cadences (one-shot, interval, cron) override
/// <see cref="ComputeNextFire"/> to project the next occurrence; the static
/// factories build the canonical cadence shapes. Per the TimeProvider rule
/// (DR-7), <see cref="ComputeNextFire"/> receives the current instant as a
/// parameter and never reads a clock of its own, and the factories perform no
/// clock access — they only capture their inputs.
/// </remarks>
public abstract record Cadence
{
    /// <summary>
    /// Computes the next instant this cadence is due to fire, or
    /// <see langword="null"/> when no further occurrence is scheduled.
    /// </summary>
    /// <param name="lastFiredAt">
    /// The instant the job most recently fired, or <see langword="null"/> if it
    /// has never fired.
    /// </param>
    /// <param name="now">
    /// The current instant, supplied by the caller (DR-7) — implementations must
    /// never read a clock of their own.
    /// </param>
    /// <returns>
    /// The next fire instant, or <see langword="null"/> when the cadence is
    /// exhausted (for example, a one-shot that has already fired).
    /// </returns>
    public abstract DateTimeOffset? ComputeNextFire(DateTimeOffset? lastFiredAt, DateTimeOffset now);

    /// <summary>
    /// Builds a recurring cadence that fires once per <paramref name="interval"/>.
    /// </summary>
    /// <param name="interval">The spacing between occurrences; must be positive.</param>
    /// <returns>An <see cref="IntervalCadence"/> for the supplied interval.</returns>
    public static Cadence Interval(TimeSpan interval) => new IntervalCadence(interval);

    /// <summary>
    /// Builds a cadence from a cron expression.
    /// </summary>
    /// <param name="expression">The cron expression to parse.</param>
    /// <param name="timeZone">
    /// The time zone the expression is evaluated in, or <see langword="null"/> to
    /// evaluate in UTC.
    /// </param>
    /// <returns>A cron cadence for the supplied expression.</returns>
    /// <remarks>
    /// Temporarily unimplemented: the <c>CronCadence</c> type and this wiring are
    /// introduced in Task 12. This stub exists only so the one-shot factories can
    /// compile in Task 9 and is replaced when cron support lands.
    /// </remarks>
    public static Cadence Cron(string expression, TimeZoneInfo? timeZone = null)
        => throw new NotImplementedException();

    /// <summary>
    /// Builds a one-shot cadence that fires once at the supplied absolute instant.
    /// </summary>
    /// <param name="fireAt">The instant the job fires.</param>
    /// <returns>A <see cref="OneShotCadence"/> for the supplied instant.</returns>
    public static Cadence At(DateTimeOffset fireAt) => new OneShotCadence(fireAt);

    /// <summary>
    /// Builds an unresolved one-shot cadence that fires once after the supplied
    /// delay. This factory performs no clock access (R1): it merely captures the
    /// delay. The registry resolves it to an absolute <see cref="OneShotCadence"/>
    /// against the scheduler clock at registration time.
    /// </summary>
    /// <param name="delay">The delay before the single fire.</param>
    /// <returns>A <see cref="RelativeOneShotCadence"/> wrapping the delay.</returns>
    public static Cadence After(TimeSpan delay) => new RelativeOneShotCadence(delay);
}
