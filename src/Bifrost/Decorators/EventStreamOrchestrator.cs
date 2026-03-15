// =============================================================================
// <copyright file="EventStreamOrchestrator.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Bifrost.Core;
using Bifrost.Core.Events;

using Microsoft.Extensions.Logging;

namespace Bifrost.Decorators;

/// <summary>
/// Decorator that publishes events for work lifecycle operations.
/// </summary>
/// <typeparam name="TWork">The type of work item to process.</typeparam>
/// <remarks>
/// <para>
/// This decorator wraps an <see cref="IWorkOrchestrator{TWork}"/> and publishes
/// events for key lifecycle operations like enqueue and completion. Events are
/// published to multiple subscriber channels via the broadcast pattern.
/// </para>
/// <para>
/// Each subscriber gets their own bounded channel using <see cref="BoundedChannelFullMode.DropOldest"/>
/// to prevent slow subscribers from blocking others or causing memory issues.
/// Subscribers are automatically cleaned up when their enumeration is cancelled or disposed.
/// </para>
/// </remarks>
public sealed class EventStreamOrchestrator<TWork> : IEventStreamOrchestrator<TWork>
{
    private const int SubscriberCapacity = 1000;

    private readonly IWorkOrchestrator<TWork> _inner;
    private readonly ILogger<EventStreamOrchestrator<TWork>> _logger;
    private readonly ConcurrentDictionary<Guid, Channel<IOrchestratorEvent>> _eventSubscribers = new();
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventStreamOrchestrator{TWork}"/> class.
    /// </summary>
    /// <param name="inner">The inner orchestrator to wrap.</param>
    /// <param name="logger">The logger instance.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="inner"/> or <paramref name="logger"/> is null.</exception>
    public EventStreamOrchestrator(
        IWorkOrchestrator<TWork> inner,
        ILogger<EventStreamOrchestrator<TWork>> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(logger);

        _inner = inner;
        _logger = logger;
    }

    /// <inheritdoc/>
    public int PendingCount => _inner.PendingCount;

    /// <inheritdoc/>
    public int ActiveWorkers => _inner.ActiveWorkers;

    /// <inheritdoc/>
    public int Capacity => _inner.Capacity;

    /// <inheritdoc/>
    public ChannelWriter<TWork> Writer => _inner.Writer;

    /// <inheritdoc/>
    public async ValueTask EnqueueAsync(TWork work, CancellationToken ct = default)
    {
        await _inner.EnqueueAsync(work, ct).ConfigureAwait(false);

        var evt = new WorkEnqueuedEvent<TWork>(work, DateTimeOffset.UtcNow, PendingCount);
        PublishToSubscribers(evt);
    }

    /// <inheritdoc/>
    public async ValueTask EnqueueAsync(TWork work, string? correlationId, CancellationToken ct = default)
    {
        await _inner.EnqueueAsync(work, ct).ConfigureAwait(false);

        var evt = new WorkEnqueuedEvent<TWork>(work, DateTimeOffset.UtcNow, PendingCount, correlationId);
        PublishToSubscribers(evt);
    }

    /// <inheritdoc/>
    public bool TryEnqueue(TWork work)
    {
        var result = _inner.TryEnqueue(work);

        if (result)
        {
            var evt = new WorkEnqueuedEvent<TWork>(work, DateTimeOffset.UtcNow, PendingCount);
            PublishToSubscribers(evt);
        }

        return result;
    }

    /// <inheritdoc/>
    public bool TryEnqueue(TWork work, string? correlationId)
    {
        var result = _inner.TryEnqueue(work);

        if (result)
        {
            var evt = new WorkEnqueuedEvent<TWork>(work, DateTimeOffset.UtcNow, PendingCount, correlationId);
            PublishToSubscribers(evt);
        }

        return result;
    }

    /// <inheritdoc/>
    public void Run(TWork work)
    {
        _inner.Run(work);

        var evt = new WorkEnqueuedEvent<TWork>(work, DateTimeOffset.UtcNow, PendingCount);
        PublishToSubscribers(evt);
    }

    /// <inheritdoc/>
    public bool TryRun(TWork work)
    {
        var result = _inner.TryRun(work);

        if (result)
        {
            var evt = new WorkEnqueuedEvent<TWork>(work, DateTimeOffset.UtcNow, PendingCount);
            PublishToSubscribers(evt);
        }

        return result;
    }

    /// <inheritdoc/>
    public Func<string, CancellationToken, Task> CreateWorkerFunction()
        => _inner.CreateWorkerFunction();

    /// <inheritdoc/>
    public Func<string, CancellationToken, Task> CreateWorkerFunction(Action<bool>? stateCallback)
        => _inner.CreateWorkerFunction(stateCallback);

    /// <inheritdoc/>
    public Task RequestScaleUpAsync(int count, CancellationToken cancellationToken = default)
        => _inner.RequestScaleUpAsync(count, cancellationToken);

    /// <inheritdoc/>
    public Task RequestScaleDownAsync(int count, CancellationToken cancellationToken = default)
        => _inner.RequestScaleDownAsync(count, cancellationToken);

    /// <inheritdoc/>
    public CancellationToken GetShutdownToken()
        => _inner.GetShutdownToken();

    /// <inheritdoc/>
    public Task DrainAsync(CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken ct = default)
    {
        return _inner.StopAsync(ct);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _disposed = true;

        // Complete all subscriber channels
        foreach (var subscriber in _eventSubscribers.Values)
        {
            subscriber.Writer.TryComplete();
        }

        _eventSubscribers.Clear();

        return _inner.DisposeAsync();
    }

    /// <inheritdoc/>
    /// <exception cref="ObjectDisposedException">Thrown when the orchestrator has been disposed.</exception>
    public IAsyncEnumerable<TEvent> GetEventStreamAsync<TEvent>(
        string? correlationId = null,
        CancellationToken cancellationToken = default)
        where TEvent : IOrchestratorEvent
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var subscriberId = Guid.NewGuid();
        var subscriberChannel = Channel.CreateBounded<IOrchestratorEvent>(
            new BoundedChannelOptions(SubscriberCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

        _eventSubscribers.TryAdd(subscriberId, subscriberChannel);

        return ReadWithCleanup<TEvent>(subscriberId, subscriberChannel.Reader, correlationId, cancellationToken);
    }

    private async IAsyncEnumerable<TEvent> ReadWithCleanup<TEvent>(
        Guid subscriberId,
        ChannelReader<IOrchestratorEvent> reader,
        string? correlationId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TEvent : IOrchestratorEvent
    {
        try
        {
            await foreach (var evt in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // Type filtering
                if (evt is not TEvent typedEvent)
                {
                    continue;
                }

                // Correlation ID filtering (only when correlationId is specified)
                if (correlationId is not null)
                {
                    if (evt is ICorrelatedEvent correlated)
                    {
                        if (!string.Equals(correlated.CorrelationId, correlationId, StringComparison.Ordinal))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        // Event doesn't implement ICorrelatedEvent, skip when filtering by correlationId
                        continue;
                    }
                }

                yield return typedEvent;
            }
        }
        finally
        {
            // Cleanup subscriber on completion or cancellation (M13: log on failure)
            if (_eventSubscribers.TryRemove(subscriberId, out var channel))
            {
                channel.Writer.TryComplete();
            }
        }
    }

    /// <summary>
    /// Publishes an event to all subscribers. Also called by <see cref="CompletionTrackingHandler"/>
    /// to publish <see cref="WorkCompletedEvent{TWork}"/> events.
    /// </summary>
    /// <param name="evt">The event to publish.</param>
    internal void PublishToSubscribers(IOrchestratorEvent evt)
    {
        if (_disposed)
        {
            return;
        }

        foreach (var subscriber in _eventSubscribers.Values)
        {
            // Non-blocking write - DropOldest ensures we don't block
            subscriber.Writer.TryWrite(evt);
        }

        // M10: Clean up stale subscribers whose buffers are completely full.
        // A full buffer indicates the consumer is not reading events.
        foreach (var (id, channel) in _eventSubscribers)
        {
            if (channel.Reader.CanCount && channel.Reader.Count >= SubscriberCapacity)
            {
                if (_eventSubscribers.TryRemove(id, out var staleChannel))
                {
                    staleChannel.Writer.TryComplete();
                    _logger.LogWarning("Removed stale event subscriber {SubscriberId} — buffer full", id);
                }
            }
        }
    }

    /// <summary>
    /// Creates a handler decorator that tracks work completion and publishes
    /// <see cref="WorkCompletedEvent{TWork}"/> events to the event stream.
    /// </summary>
    /// <param name="innerHandler">The handler to wrap.</param>
    /// <param name="orchestrator">The event stream orchestrator to publish events to.</param>
    /// <returns>A handler that wraps the inner handler with completion tracking.</returns>
    /// <exception cref="ArgumentNullException">Thrown when parameters are null.</exception>
    public static IWorkHandler<TWork> CreateCompletionTrackingHandler(
        IWorkHandler<TWork> innerHandler,
        EventStreamOrchestrator<TWork> orchestrator)
    {
        ArgumentNullException.ThrowIfNull(innerHandler);
        ArgumentNullException.ThrowIfNull(orchestrator);

        return new CompletionTrackingHandler(innerHandler, evt => orchestrator.PublishToSubscribers(evt));
    }

    /// <summary>
    /// Creates a handler decorator that tracks work completion and publishes
    /// <see cref="WorkCompletedEvent{TWork}"/> events via a callback. This overload
    /// supports late-binding scenarios where the orchestrator instance is not yet
    /// available at handler construction time (e.g., during DI registration).
    /// </summary>
    /// <param name="innerHandler">The handler to wrap.</param>
    /// <param name="publishCallback">Callback invoked to publish completion events.</param>
    /// <returns>A handler that wraps the inner handler with completion tracking.</returns>
    /// <exception cref="ArgumentNullException">Thrown when parameters are null.</exception>
    internal static IWorkHandler<TWork> CreateCompletionTrackingHandler(
        IWorkHandler<TWork> innerHandler,
        Action<IOrchestratorEvent> publishCallback)
    {
        ArgumentNullException.ThrowIfNull(innerHandler);
        ArgumentNullException.ThrowIfNull(publishCallback);

        return new CompletionTrackingHandler(innerHandler, publishCallback);
    }

    /// <summary>
    /// Handler decorator that instruments work processing with timing and publishes
    /// <see cref="WorkCompletedEvent{TWork}"/> events on completion.
    /// </summary>
    private sealed class CompletionTrackingHandler : IWorkHandler<TWork>
    {
        private readonly IWorkHandler<TWork> _innerHandler;
        private readonly Action<IOrchestratorEvent> _publishCallback;

        public CompletionTrackingHandler(
            IWorkHandler<TWork> innerHandler,
            Action<IOrchestratorEvent> publishCallback)
        {
            _innerHandler = innerHandler;
            _publishCallback = publishCallback;
        }

        public async ValueTask HandleAsync(TWork work, CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await _innerHandler.HandleAsync(work, ct).ConfigureAwait(false);
                stopwatch.Stop();
                _publishCallback(
                    new WorkCompletedEvent<TWork>(work, stopwatch.Elapsed, Success: true));
            }
            catch
            {
                stopwatch.Stop();
                _publishCallback(
                    new WorkCompletedEvent<TWork>(work, stopwatch.Elapsed, Success: false));
                throw;
            }
        }
    }
}