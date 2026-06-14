// =============================================================================
// <copyright file="OneShotCadence.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Core;

/// <summary>
/// A cadence that fires exactly once, at an absolute instant.
/// </summary>
/// <param name="FireAt">The instant the job fires.</param>
/// <remarks>
/// One-shots do not re-fire: once <see cref="JobRecord.LastFiredAt"/> is set,
/// <see cref="ComputeNextFire"/> returns <see langword="null"/>. A one-shot whose
/// instant is already in the past still surfaces that instant — the tick loop, not
/// the cadence, decides how to reconcile a past fire against the missed-fire policy.
/// </remarks>
public sealed record OneShotCadence(DateTimeOffset FireAt) : Cadence
{
    /// <inheritdoc/>
    public override DateTimeOffset? ComputeNextFire(DateTimeOffset? lastFiredAt, DateTimeOffset now)
        => lastFiredAt is null ? FireAt : null;
}
