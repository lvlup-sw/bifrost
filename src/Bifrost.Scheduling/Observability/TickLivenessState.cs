// =============================================================================
// <copyright file="TickLivenessState.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Observability;

/// <summary>
/// Discriminates the scheduler loop's liveness phases so "unstarted", "waiting for
/// first tick", "parked with no deadline", "parked until a deadline", and "active"
/// are distinct values and no sentinel can collapse them.
/// </summary>
internal enum TickLivenessKind
{
    /// <summary>
    /// Observation has not started and the loop has not entered execution yet.
    /// </summary>
    Unstarted,

    /// <summary>
    /// Observation began at <see cref="TickLivenessState.Timestamp"/> and the loop has
    /// not yet recorded its first tick.
    /// </summary>
    WaitingForFirstTick,

    /// <summary>
    /// The loop is actively ticking; <see cref="TickLivenessState.Timestamp"/> holds
    /// the most recent tick instant.
    /// </summary>
    Active,

    /// <summary>
    /// The loop is deliberately parked on an empty heap waiting for a command.
    /// </summary>
    IdleEmpty,

    /// <summary>
    /// The loop is deliberately parked until <see cref="TickLivenessState.Timestamp"/>.
    /// </summary>
    IdleUntil,
}

/// <summary>
/// Discriminated union representing the tick loop's liveness state for
/// <see cref="SchedulerHealthCheck"/>.
/// </summary>
/// <param name="Kind">The current liveness phase.</param>
/// <param name="Timestamp">
/// The observation start instant for <see cref="TickLivenessKind.WaitingForFirstTick"/>,
/// the last tick instant for <see cref="TickLivenessKind.Active"/>, the wake deadline
/// for <see cref="TickLivenessKind.IdleUntil"/>, or <see langword="null"/> otherwise.
/// </param>
internal readonly record struct TickLivenessState(TickLivenessKind Kind, DateTimeOffset? Timestamp)
{
    /// <summary>Gets the unstarted state.</summary>
    public static TickLivenessState Unstarted { get; } = new(TickLivenessKind.Unstarted, null);

    /// <summary>Gets the empty-heap idle state.</summary>
    public static TickLivenessState IdleEmpty { get; } = new(TickLivenessKind.IdleEmpty, null);

    /// <summary>
    /// Creates a state indicating observation started at <paramref name="observingSince"/>
    /// and the first tick has not arrived yet.
    /// </summary>
    /// <param name="observingSince">The instant observation started.</param>
    /// <returns>The waiting-for-first-tick state.</returns>
    public static TickLivenessState WaitingForFirstTick(DateTimeOffset observingSince)
        => new(TickLivenessKind.WaitingForFirstTick, observingSince);

    /// <summary>
    /// Creates a state indicating the loop ticked at <paramref name="lastTickAt"/>.
    /// </summary>
    /// <param name="lastTickAt">The most recent tick instant.</param>
    /// <returns>The active liveness state.</returns>
    public static TickLivenessState Active(DateTimeOffset lastTickAt)
        => new(TickLivenessKind.Active, lastTickAt);

    /// <summary>
    /// Creates a state indicating the loop is parked until <paramref name="deadline"/>.
    /// </summary>
    /// <param name="deadline">The wake deadline.</param>
    /// <returns>The idle-until state.</returns>
    public static TickLivenessState IdleUntil(DateTimeOffset deadline)
        => new(TickLivenessKind.IdleUntil, deadline);
}
