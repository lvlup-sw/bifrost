// =============================================================================
// <copyright file="IntervalCadence.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Core;

/// <summary>
/// A recurring cadence that fires once per fixed <see cref="Interval"/>.
/// </summary>
/// <param name="Interval">The spacing between occurrences; must be strictly positive.</param>
/// <param name="Jitter">
/// The optional jitter fraction in <c>[0, 1]</c> applied to each computed fire to
/// spread load; <c>0</c> disables jitter.
/// </param>
/// <remarks>
/// Occurrences are aligned to whole-interval steps from the last fire. When one or
/// more intervals were missed (for example after a pause), <see cref="ComputeNextFire"/>
/// advances to the next aligned occurrence strictly after <c>now</c> rather than
/// replaying the backlog — backlog reconciliation is the missed-fire policy's job.
/// Validation lives in the <c>init</c> accessors so it applies to both construction
/// and non-destructive <c>with</c> mutation.
/// </remarks>
public sealed record IntervalCadence(TimeSpan Interval, double Jitter = 0) : Cadence
{
    private readonly TimeSpan interval = ValidateInterval(Interval);
    private readonly double jitter = ValidateJitter(Jitter);

    /// <summary>
    /// Gets the spacing between occurrences; always strictly positive.
    /// </summary>
    /// <remarks>
    /// The <c>new</c> modifier intentionally shadows the unrelated static
    /// <see cref="Cadence.Interval(TimeSpan)"/> factory on the base record; the two
    /// share a name by domain convention but never collide at a call site.
    /// </remarks>
    public new TimeSpan Interval
    {
        get => this.interval;
        init => this.interval = ValidateInterval(value);
    }

    /// <summary>
    /// Gets the jitter fraction in <c>[0, 1]</c> applied to each computed fire;
    /// <c>0</c> disables jitter.
    /// </summary>
    public double Jitter
    {
        get => this.jitter;
        init => this.jitter = ValidateJitter(value);
    }

    /// <inheritdoc/>
    public override DateTimeOffset? ComputeNextFire(DateTimeOffset? lastFiredAt, DateTimeOffset now)
    {
        var baseFire = ComputeBaseFire(lastFiredAt, now);
        return ApplyJitter(baseFire);
    }

    private static TimeSpan ValidateInterval(TimeSpan value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(Interval));
        return value;
    }

    private static double ValidateJitter(double value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(Jitter));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 1.0, nameof(Jitter));
        return value;
    }

    /// <summary>
    /// Computes the un-jittered next aligned occurrence strictly after <c>now</c>.
    /// </summary>
    /// <param name="lastFiredAt">The last fire instant, or <see langword="null"/>.</param>
    /// <param name="now">The current instant (DR-7).</param>
    /// <returns>The next aligned occurrence, strictly greater than <c>now</c>.</returns>
    private DateTimeOffset ComputeBaseFire(DateTimeOffset? lastFiredAt, DateTimeOffset now)
    {
        if (lastFiredAt is null)
        {
            return now + this.interval;
        }

        var candidate = lastFiredAt.Value + this.interval;
        if (candidate > now)
        {
            return candidate;
        }

        // One or more intervals were missed: jump to the next aligned occurrence
        // strictly after now in a single arithmetic step (no per-interval loop).
        var elapsed = now - lastFiredAt.Value;
        var steps = (elapsed.Ticks / this.interval.Ticks) + 1;
        return lastFiredAt.Value + TimeSpan.FromTicks(this.interval.Ticks * steps);
    }

    /// <summary>
    /// Applies the configured <see cref="Jitter"/> to a base fire instant. When
    /// jitter is zero this is the identity. Jitter is symmetric: the deviation is at
    /// most <see cref="Interval"/> × <see cref="Jitter"/> on either side.
    /// </summary>
    /// <param name="baseFire">The un-jittered fire instant.</param>
    /// <returns>The jittered fire instant.</returns>
    private DateTimeOffset ApplyJitter(DateTimeOffset baseFire)
    {
        if (this.jitter <= 0)
        {
            return baseFire;
        }

        // Symmetric offset in [-Jitter, +Jitter] of the interval. Production
        // randomness uses the shared RNG; tests assert only the deterministic bounds.
        var fraction = (Random.Shared.NextDouble() * 2.0) - 1.0;
        var offsetTicks = (long)(this.interval.Ticks * this.jitter * fraction);
        return baseFire + TimeSpan.FromTicks(offsetTicks);
    }
}
