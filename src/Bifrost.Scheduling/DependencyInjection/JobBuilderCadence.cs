// =============================================================================
// <copyright file="JobBuilderCadence.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;

namespace Bifrost.Scheduling.DependencyInjection;

/// <summary>
/// Cadence-mutation helpers shared by the orchestrator and inline job builders, so
/// the cadence DSL behaves identically across both.
/// </summary>
internal static class JobBuilderCadence
{
    /// <summary>
    /// Applies a jitter fraction to the current cadence. Jitter is only meaningful
    /// for an <see cref="IntervalCadence"/>; applying it to any other cadence (or
    /// before a cadence is set) is a configuration error.
    /// </summary>
    /// <param name="current">The job builder's current cadence, or <see langword="null"/>.</param>
    /// <param name="fraction">The jitter fraction in <c>[0, 1]</c>.</param>
    /// <returns>The interval cadence with the jitter fraction applied.</returns>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="current"/> is not an <see cref="IntervalCadence"/>.
    /// </exception>
    public static Cadence ApplyJitter(Cadence? current, double fraction)
    {
        if (current is not IntervalCadence interval)
        {
            throw new InvalidOperationException(
                "WithJitter can only be applied to an interval cadence; call Every before WithJitter.");
        }

        return interval.WithJitter(fraction);
    }
}
