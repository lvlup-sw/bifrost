// =============================================================================
// <copyright file="NullSchedulerEventSink.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;

namespace Bifrost.Scheduling.Observability;

/// <summary>
/// An <see cref="ISchedulerEventSink"/> that discards every published event. Used
/// as the default when a component is constructed without an explicit sink — for
/// example a registry built in a test that does not observe events — so the
/// publication path is always safe to call without a null check.
/// </summary>
internal sealed class NullSchedulerEventSink : ISchedulerEventSink
{
    /// <summary>
    /// The shared singleton instance; the sink is stateless, so one instance serves
    /// every caller.
    /// </summary>
    public static readonly NullSchedulerEventSink Instance = new();

    private NullSchedulerEventSink()
    {
    }

    /// <inheritdoc/>
    public void Publish<TEvent>(in TEvent schedulerEvent)
        where TEvent : struct
    {
        // Intentionally discards the event.
    }
}
