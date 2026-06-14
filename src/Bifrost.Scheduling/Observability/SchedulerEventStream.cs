// =============================================================================
// <copyright file="SchedulerEventStream.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Bifrost.Scheduling.Core;

namespace Bifrost.Scheduling.Observability;

/// <summary>
/// The concrete <see cref="ISchedulerEventSink"/> (DR-8): a broadcast fan-out that
/// surfaces every scheduler event published by the registry and the tick loop to
/// any number of typed subscribers. A single shared instance is wired into both
/// the <c>ScheduleRegistry</c> (lifecycle events) and the <c>ScheduleTickLoop</c>
/// (fire, failure, missed-fire, and fault events), so a consumer subscribed here
/// observes the full event timeline.
/// </summary>
/// <remarks>
/// <para>
/// The design mirrors the established Bifrost event-stream idiom (see
/// <c>EventStreamOrchestrator</c>): each subscriber gets an independent bounded
/// channel with <see cref="BoundedChannelFullMode.DropOldest"/> so a slow consumer
/// never blocks a fire or another subscriber. Subscriptions are consumed as an
/// <see cref="IAsyncEnumerable{T}"/> filtered to the requested event type and are
/// cleaned up automatically when their enumeration is cancelled or disposed.
/// </para>
/// <para>
/// <see cref="Publish{TEvent}(in TEvent)"/> boxes the struct event once into the
/// shared <see cref="object"/> channel element type, then fans it out without
/// further allocation. Boxing here is the unavoidable cost of a heterogeneous,
/// reflection-free fan-out; the no-boxing guarantee of the <c>in</c> seam holds at
/// the call site, and a scheduler publishes at human cadence, not a hot path. The
/// implementation is fully AOT-safe: no reflection, no runtime codegen.
/// </para>
/// </remarks>
public sealed class SchedulerEventStream : ISchedulerEventSink, IDisposable
{
    /// <summary>
    /// The per-subscriber buffer capacity. A subscriber that falls this far behind
    /// drops its oldest buffered events rather than blocking the publisher.
    /// </summary>
    private const int SubscriberCapacity = 1024;

    private readonly ConcurrentDictionary<Guid, Channel<object>> subscribers = new();
    private volatile bool disposed;

    /// <summary>
    /// Publishes a scheduler event to every current subscriber. Boxes the value-type
    /// event once and writes it non-blocking to each subscriber channel.
    /// </summary>
    /// <typeparam name="TEvent">The scheduler event type.</typeparam>
    /// <param name="schedulerEvent">The event to publish.</param>
    public void Publish<TEvent>(in TEvent schedulerEvent)
        where TEvent : struct
    {
        if (this.disposed)
        {
            return;
        }

        // Box once into the shared object channel; the typed Subscribe path unboxes
        // by pattern match. A scheduler event is published at human cadence, so this
        // single box per publish is not a hot-path concern.
        object boxed = schedulerEvent;
        foreach (var subscriber in this.subscribers.Values)
        {
            // Non-blocking: DropOldest guarantees a slow subscriber never blocks a fire.
            subscriber.Writer.TryWrite(boxed);
        }
    }

    /// <summary>
    /// Subscribes to scheduler events of a single type, returned as an async stream
    /// that yields each matching event until the subscription is cancelled.
    /// </summary>
    /// <typeparam name="TEvent">The scheduler event type to observe.</typeparam>
    /// <param name="cancellationToken">A token that ends the subscription.</param>
    /// <returns>An async enumerable of events of type <typeparamref name="TEvent"/>.</returns>
    /// <exception cref="ObjectDisposedException">The stream has been disposed.</exception>
    public IAsyncEnumerable<TEvent> Subscribe<TEvent>(CancellationToken cancellationToken = default)
        where TEvent : struct
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        var subscriberId = Guid.NewGuid();
        var channel = Channel.CreateBounded<object>(
            new BoundedChannelOptions(SubscriberCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

        this.subscribers.TryAdd(subscriberId, channel);

        return this.ReadWithCleanup<TEvent>(subscriberId, channel.Reader, cancellationToken);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        foreach (var subscriber in this.subscribers.Values)
        {
            subscriber.Writer.TryComplete();
        }

        this.subscribers.Clear();
    }

    private async IAsyncEnumerable<TEvent> ReadWithCleanup<TEvent>(
        Guid subscriberId,
        ChannelReader<object> reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TEvent : struct
    {
        try
        {
            await foreach (var evt in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (evt is TEvent typed)
                {
                    yield return typed;
                }
            }
        }
        finally
        {
            // Remove and complete the subscriber on enumeration end or cancellation, so
            // a dropped consumer never receives further fan-out.
            if (this.subscribers.TryRemove(subscriberId, out var channel))
            {
                channel.Writer.TryComplete();
            }
        }
    }
}
