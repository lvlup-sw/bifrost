// =============================================================================
// <copyright file="RecordingSchedulerEventSink.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;

namespace Bifrost.Tests.Scheduling;

/// <summary>
/// A test double for <see cref="ISchedulerEventSink"/> that records every
/// published event for later assertion. Boxes each struct event into the
/// recorded list — acceptable in tests, where the production no-boxing path
/// is irrelevant. Recording is lock-guarded so dispatches handed off to the
/// thread pool can be observed without a data race.
/// </summary>
public sealed class RecordingSchedulerEventSink : ISchedulerEventSink
{
    private readonly object gate = new();
    private readonly List<object> published = [];

    /// <summary>
    /// Gets a snapshot of the events published so far, oldest first.
    /// </summary>
    public IReadOnlyList<object> Published
    {
        get
        {
            lock (this.gate)
            {
                return [.. this.published];
            }
        }
    }

    /// <inheritdoc/>
    public void Publish<TEvent>(in TEvent schedulerEvent)
        where TEvent : struct
    {
        lock (this.gate)
        {
            this.published.Add(schedulerEvent);
        }
    }

    /// <summary>
    /// Returns the single recorded event of type <typeparamref name="TEvent"/>,
    /// failing if zero or more than one was published.
    /// </summary>
    /// <typeparam name="TEvent">The event type to extract.</typeparam>
    /// <returns>The single recorded event of that type.</returns>
    public TEvent Single<TEvent>()
        where TEvent : struct
    {
        lock (this.gate)
        {
            return this.published.OfType<TEvent>().Single();
        }
    }

    /// <summary>
    /// Returns whether any event of type <typeparamref name="TEvent"/> was published.
    /// </summary>
    /// <typeparam name="TEvent">The event type to look for.</typeparam>
    /// <returns><see langword="true"/> if at least one was published.</returns>
    public bool Any<TEvent>()
        where TEvent : struct
    {
        lock (this.gate)
        {
            return this.published.OfType<TEvent>().Any();
        }
    }
}
