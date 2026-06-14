// =============================================================================
// <copyright file="ISchedulerEventSink.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.Core;

/// <summary>
/// The publication seam scheduler components push events through. Dispatchers,
/// the dispatch router, the tick loop, and the registry all surface their
/// lifecycle events — <see cref="Bifrost.Scheduling.Core.Events.JobFiredEvent"/>,
/// <see cref="Bifrost.Scheduling.Core.Events.JobFireFailedEvent"/>, and the
/// rest — by publishing them here, rather than depending on any concrete
/// fan-out mechanism.
/// </summary>
/// <remarks>
/// <para>
/// This is a deliberately minimal forward-compatible contract. The concrete
/// sink — an event-stream fan-out to subscribers — lands in a later group; until
/// then this seam lets the dispatch layer surface a failed fire as a
/// <see cref="Bifrost.Scheduling.Core.Events.JobFireFailedEvent"/> without
/// throwing, and without taking a dependency on the fan-out implementation.
/// </para>
/// <para>
/// <see cref="Publish{TEvent}(in TEvent)"/> takes its event by
/// <see langword="in"/> reference and constrains <c>TEvent</c> to a value type,
/// so a caller publishing one of the readonly-record-struct scheduler events
/// does so without boxing. Implementations must be safe to call from any thread,
/// including a thread-pool dispatch handed off by the router.
/// </para>
/// </remarks>
public interface ISchedulerEventSink
{
    /// <summary>
    /// Publishes a scheduler event to whatever consumers the concrete sink
    /// fans out to.
    /// </summary>
    /// <typeparam name="TEvent">
    /// The scheduler event type — one of the readonly-record-struct events in
    /// <see cref="Bifrost.Scheduling.Core.Events"/>.
    /// </typeparam>
    /// <param name="schedulerEvent">The event to publish, passed by reference to avoid boxing.</param>
    void Publish<TEvent>(in TEvent schedulerEvent)
        where TEvent : struct;
}
