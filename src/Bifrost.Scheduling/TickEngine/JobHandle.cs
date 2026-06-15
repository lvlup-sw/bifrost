// =============================================================================
// <copyright file="JobHandle.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;

namespace Bifrost.Scheduling.TickEngine;

/// <summary>
/// The tick loop's in-heap representation of a single scheduled job: the minimum
/// state the loop needs to evaluate a fire — the job name, its cadence, and its
/// missed-fire policy — plus the scheduled occurrence this heap entry represents.
/// </summary>
/// <remarks>
/// <para>
/// The live <see cref="IJobDispatcher"/> is intentionally <em>not</em> captured on
/// the handle: the loop resolves it from the registry at the moment of dispatch
/// (<see cref="ScheduleTickLoop"/>), so a job whose dispatcher is re-registered is
/// always dispatched through its current dispatcher rather than a stale one.
/// </para>
/// <para>
/// <see cref="Generation"/> supports lazy deletion from the
/// <see cref="System.Collections.Generic.PriorityQueue{TElement, TPriority}"/>:
/// because a priority queue cannot remove an arbitrary element, a pause, removal,
/// or reschedule bumps the job's live generation and leaves the old entry in the
/// heap. When the loop later pops a stale entry — one whose generation no longer
/// matches the job's live generation — it discards it instead of firing.
/// </para>
/// </remarks>
internal sealed record JobHandle(
    string JobName,
    Cadence Cadence,
    MissedFirePolicy MissedFirePolicy,
    DateTimeOffset ScheduledOccurrence,
    long Generation);
