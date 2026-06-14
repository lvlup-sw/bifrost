// =============================================================================
// <copyright file="CadenceComputeBenchmarks.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using BenchmarkDotNet.Attributes;

using Bifrost.Scheduling.Core;

namespace Bifrost.Benchmarks.Scheduling;

/// <summary>
/// Benchmarks for <see cref="Cadence.ComputeNextFire"/> across all cadence types
/// (DR-2, DR-11, Task 43).
/// </summary>
/// <remarks>
/// <para>
/// Each benchmark calls <c>ComputeNextFire</c> on a pre-built cadence instance,
/// passing a fixed <c>now</c> so the measurement captures only the cadence
/// arithmetic — no clock access, no allocation overhead from construction.
/// </para>
/// <para>
/// Cron benchmarks cover both the cheapest cron case (every-minute: zero field
/// arithmetic) and a representative complex expression with multiple restricted
/// fields (every 15 minutes, weekdays only, between 08:00–18:00) to show the
/// Cronos parsing overhead under realistic schedules.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class CadenceComputeBenchmarks
{
    // Fixed reference instant used as both lastFiredAt and now across all benchmarks.
    private static readonly DateTimeOffset Epoch =
        new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero); // A Monday at noon UTC

    // Pre-built cadence instances (construction cost is not measured).
    private readonly IntervalCadence _interval =
        (IntervalCadence)Cadence.Interval(TimeSpan.FromMinutes(5));

    private readonly IntervalCadence _intervalWithJitter =
        ((IntervalCadence)Cadence.Interval(TimeSpan.FromMinutes(5))).WithJitter(0.1);

    private readonly CronCadence _cronEveryMinute =
        (CronCadence)Cadence.Cron("* * * * *");

    // Complex expression: every 15 minutes, Mon–Fri, 08:00–18:00 UTC
    private readonly CronCadence _cronComplex =
        (CronCadence)Cadence.Cron("*/15 8-18 * * 1-5");

    private readonly OneShotCadence _oneShot =
        (OneShotCadence)Cadence.At(Epoch.AddHours(1));

    /// <summary>
    /// Measures <see cref="IntervalCadence.ComputeNextFire"/> for a plain
    /// 5-minute interval with no jitter applied.
    /// </summary>
    /// <returns>The computed next-fire instant; returned to prevent dead-code elimination.</returns>
    [Benchmark(Baseline = true)]
    public DateTimeOffset? Interval_ComputeNextFire()
    {
        return _interval.ComputeNextFire(lastFiredAt: Epoch, now: Epoch);
    }

    /// <summary>
    /// Measures <see cref="IntervalCadence.ComputeNextFire"/> with a 10% jitter
    /// fraction applied. Captures the cost of the <c>Random.Shared</c> call and
    /// ticks arithmetic relative to the no-jitter baseline.
    /// </summary>
    /// <returns>The computed next-fire instant; returned to prevent dead-code elimination.</returns>
    [Benchmark]
    public DateTimeOffset? IntervalWithJitter_ComputeNextFire()
    {
        return _intervalWithJitter.ComputeNextFire(lastFiredAt: Epoch, now: Epoch);
    }

    /// <summary>
    /// Measures <see cref="CronCadence.ComputeNextFire"/> for the simplest cron
    /// expression (<c>* * * * *</c>, every minute). This is the lower-bound cost
    /// of a Cronos <c>GetNextOccurrence</c> call.
    /// </summary>
    /// <returns>The computed next-fire instant; returned to prevent dead-code elimination.</returns>
    [Benchmark]
    public DateTimeOffset? Cron_ComputeNextFire_EveryMinute()
    {
        return _cronEveryMinute.ComputeNextFire(lastFiredAt: null, now: Epoch);
    }

    /// <summary>
    /// Measures <see cref="CronCadence.ComputeNextFire"/> for a complex
    /// restricted-field expression (<c>*/15 8-18 * * 1-5</c>). Captures Cronos
    /// overhead when more fields must be evaluated.
    /// </summary>
    /// <returns>The computed next-fire instant; returned to prevent dead-code elimination.</returns>
    [Benchmark]
    public DateTimeOffset? Cron_ComputeNextFire_ComplexExpression()
    {
        return _cronComplex.ComputeNextFire(lastFiredAt: null, now: Epoch);
    }

    /// <summary>
    /// Measures <see cref="OneShotCadence.ComputeNextFire"/> for a one-shot that
    /// has not yet fired (<c>lastFiredAt</c> is <see langword="null"/>). The
    /// cadence simply returns its <c>FireAt</c> instant, so this is the trivial
    /// lower bound for any cadence computation.
    /// </summary>
    /// <returns>The computed next-fire instant; returned to prevent dead-code elimination.</returns>
    [Benchmark]
    public DateTimeOffset? OneShot_ComputeNextFire()
    {
        return _oneShot.ComputeNextFire(lastFiredAt: null, now: Epoch);
    }
}
