// =============================================================================
// <copyright file="CronCadence.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Cronos;

namespace Bifrost.Scheduling.Core;

/// <summary>
/// A cadence whose occurrences are defined by a standard five-field cron expression,
/// evaluated in a configurable time zone.
/// </summary>
/// <remarks>
/// The expression is parsed once at construction (fail-fast: an invalid expression
/// throws there, never at fire time). <see cref="ComputeNextFire"/> delegates to the
/// parsed Cronos expression, which is the single source of truth for DST handling —
/// skipped spring-forward occurrences do not fire and repeated fall-back occurrences
/// do not double-fire (DR-2, DR-10). When no time zone is supplied, the expression is
/// evaluated in UTC.
/// </remarks>
public sealed record CronCadence : Cadence
{
    private readonly CronExpression parsed;

    /// <summary>
    /// Initializes a new instance of the <see cref="CronCadence"/> record, parsing
    /// the cron expression up front.
    /// </summary>
    /// <param name="expression">The standard five-field cron expression.</param>
    /// <param name="timeZone">
    /// The time zone the expression is evaluated in, or <see langword="null"/> to
    /// evaluate in UTC.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="expression"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="CronFormatException">
    /// <paramref name="expression"/> is not a valid cron expression.
    /// </exception>
    public CronCadence(string expression, TimeZoneInfo? timeZone)
    {
        ArgumentNullException.ThrowIfNull(expression);

        this.Expression = expression;
        this.TimeZone = timeZone;
        this.parsed = CronExpression.Parse(expression, CronFormat.Standard);
    }

    /// <summary>
    /// Gets the standard five-field cron expression backing this cadence.
    /// </summary>
    public string Expression { get; }

    /// <summary>
    /// Gets the time zone the expression is evaluated in, or <see langword="null"/>
    /// to evaluate in UTC.
    /// </summary>
    public TimeZoneInfo? TimeZone { get; }

    /// <inheritdoc/>
    /// <remarks>
    /// Delegates to Cronos <c>GetNextOccurrence</c>, which never returns an occurrence
    /// at or before <c>now</c> and applies the zone's DST rules. <c>lastFiredAt</c> is
    /// unused: cron occurrences are absolute, so the next fire is purely a function of
    /// <c>now</c> and the expression.
    /// </remarks>
    public override DateTimeOffset? ComputeNextFire(DateTimeOffset? lastFiredAt, DateTimeOffset now)
        => this.parsed.GetNextOccurrence(now, this.TimeZone ?? TimeZoneInfo.Utc);
}
