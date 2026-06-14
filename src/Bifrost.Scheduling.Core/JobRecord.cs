// =============================================================================
// <copyright file="JobRecord.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Core;

/// <summary>
/// The durable, persisted representation of a scheduled job — the unit loaded
/// from and saved to the schedule store by the scheduler.
/// </summary>
/// <param name="Name">
/// The unique job name; the identity key under which the job is stored, looked
/// up, and triggered.
/// </param>
/// <param name="Cadence">The schedule that determines when the job fires.</param>
/// <param name="MissedFirePolicy">
/// How the scheduler reconciles occurrences missed while the job could not fire.
/// </param>
/// <param name="State">The current lifecycle state of the job.</param>
/// <param name="LastFiredAt">
/// The instant the job most recently fired, or <see langword="null"/> if it has
/// never fired.
/// </param>
/// <param name="NextFireAt">
/// The instant the job is next due to fire, or <see langword="null"/> if no
/// further occurrence is scheduled (for example, a completed job).
/// </param>
/// <param name="DispatchKind">
/// How the fire is dispatched — <c>"orchestrator"</c>, <c>"inline"</c>, or
/// <c>"custom"</c>.
/// </param>
/// <param name="DispatcherTypeName">
/// Diagnostic-only metadata naming the dispatcher type, or <see langword="null"/>.
/// This is never an activation input: the runtime resolves dispatchers from the
/// registry, never by reflecting over this name, so the record stays
/// AOT-compatible.
/// </param>
/// <param name="Metadata">
/// Opaque user-supplied key/value metadata carried with the job.
/// </param>
public sealed record JobRecord(
    string Name,
    Cadence Cadence,
    MissedFirePolicy MissedFirePolicy,
    JobState State,
    DateTimeOffset? LastFiredAt,
    DateTimeOffset? NextFireAt,
    string DispatchKind,
    string? DispatcherTypeName,
    IReadOnlyDictionary<string, string> Metadata);
