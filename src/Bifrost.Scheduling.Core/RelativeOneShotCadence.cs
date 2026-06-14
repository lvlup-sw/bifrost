// =============================================================================
// <copyright file="RelativeOneShotCadence.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Core;

/// <summary>
/// An unresolved one-shot cadence: fire once after a relative delay, with the
/// absolute instant still to be pinned against the scheduler clock.
/// </summary>
/// <param name="Delay">The delay before the single fire.</param>
/// <remarks>
/// Produced by <see cref="Cadence.After(TimeSpan)"/> without any clock access
/// (R1). The registry resolves it to an absolute <see cref="OneShotCadence"/> at
/// registration time; an unresolved relative cadence must never reach the tick
/// loop, so <see cref="ComputeNextFire"/> throws to fail fast if it ever does.
/// </remarks>
public sealed record RelativeOneShotCadence(TimeSpan Delay) : Cadence
{
    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">
    /// Always — a relative one-shot must be resolved to an absolute
    /// <see cref="OneShotCadence"/> by the registry before the tick loop evaluates
    /// it; reaching this method indicates a missing resolution step.
    /// </exception>
    public override DateTimeOffset? ComputeNextFire(DateTimeOffset? lastFiredAt, DateTimeOffset now)
        => throw new InvalidOperationException(
            "A relative one-shot cadence must be resolved to an absolute OneShotCadence " +
            "by the registry before it is evaluated; it must never reach the tick loop.");
}
