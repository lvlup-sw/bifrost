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
/// spread load; <c>0</c> disables jitter. See <see cref="WithJitter(double)"/>.
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
        return ApplyJitter(baseFire, now);
    }

    /// <summary>
    /// Returns a copy of this cadence with the supplied jitter fraction applied.
    /// </summary>
    /// <param name="fraction">
    /// The jitter fraction in <c>[0, 1]</c>: each computed fire is spread by up to
    /// <see cref="Interval"/> × <paramref name="fraction"/> on either side.
    /// </param>
    /// <returns>A new <see cref="IntervalCadence"/> carrying the jitter fraction.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="fraction"/> is outside <c>[0, 1]</c>.
    /// </exception>
    public IntervalCadence WithJitter(double fraction) => this with { Jitter = fraction };

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
    /// most <see cref="Interval"/> × <see cref="Jitter"/> on either side. The result
    /// is clamped to be strictly greater than <paramref name="now"/> so a downward
    /// draw never produces an immediately-due fire that churns the scheduler.
    /// </summary>
    /// <param name="baseFire">The un-jittered fire instant (always strictly after <paramref name="now"/>).</param>
    /// <param name="now">The current instant (DR-7) the jittered fire must stay strictly after.</param>
    /// <returns>The jittered fire instant, guaranteed strictly greater than <paramref name="now"/>.</returns>
    private DateTimeOffset ApplyJitter(DateTimeOffset baseFire, DateTimeOffset now)
    {
        if (this.jitter <= 0)
        {
            return baseFire;
        }

        // Symmetric offset in [-Jitter, +Jitter] of the interval. Production
        // randomness uses the shared RNG; tests assert only the deterministic bounds.
        var fraction = (Random.Shared.NextDouble() * 2.0) - 1.0;
        var offsetTicks = (long)(this.interval.Ticks * this.jitter * fraction);
        var jittered = baseFire + TimeSpan.FromTicks(offsetTicks);

        // A downward draw can land the jittered instant at or before now (notably when
        // baseFire is close to now or Jitter is near 1), which would fire immediately
        // and churn. Clamp strictly into the future: prefer the un-jittered baseFire
        // (always strictly after now), falling back to now + 1 tick only in the
        // degenerate case baseFire itself is not strictly after now.
        if (jittered <= now)
        {
            return baseFire > now ? baseFire : now + TimeSpan.FromTicks(1);
        }

        return jittered;
    }
}
