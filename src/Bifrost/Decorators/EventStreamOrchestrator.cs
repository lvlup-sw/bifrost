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
    private readonly object _subscriptionGate = new();
    private volatile bool _disposed;
    private volatile bool _draining;

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
    public Func<string, CancellationToken, Task> CreateWorkerFunction()
        => _inner.CreateWorkerFunction();

    /// <inheritdoc/>
    public Func<string, CancellationToken, Task> CreateWorkerFunction(Action<bool>? stateCallback)
        => _inner.CreateWorkerFunction(stateCallback);

    /// <inheritdoc/>
    public Func<string, CancellationToken, Task> CreateWorkerFunction(Action<bool>? stateCallback, CancellationToken retirementToken)
        => _inner.CreateWorkerFunction(stateCallback, retirementToken);

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
    /// <remarks>
    /// After the inner drain, the method completes and clears all subscriber channels under
    /// <c>_subscriptionGate</c>, so the step is atomic with respect to
    /// <see cref="GetEventStreamAsync{TEvent}"/>.
    /// </remarks>
    public async Task DrainAsync(CancellationToken ct = default)
    {
        lock (_subscriptionGate)
        {
            _draining = true;
        }

        await _inner.DrainAsync(ct);

        lock (_subscriptionGate)
        {
            foreach (var subscriber in _eventSubscribers.Values)
            {
                subscriber.Writer.TryComplete();
            }

            _eventSubscribers.Clear();
        }
    }

    /// <inheritdoc/>
    public Task AbortAsync(CancellationToken ct = default)
    {
        return _inner.AbortAsync(ct);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        lock (_subscriptionGate)
        {
            _disposed = true;
            foreach (var subscriber in _eventSubscribers.Values)
            {
                subscriber.Writer.TryComplete();
            }

            _eventSubscribers.Clear();
        }

        return _inner.DisposeAsync();
    }

    /// <inheritdoc/>
    /// <exception cref="ObjectDisposedException">Thrown when the orchestrator has been disposed.</exception>
    /// <remarks>
    /// Registration, drain, and disposal must be atomic under <c>_subscriptionGate</c>. Otherwise a
    /// channel that is added after the snapshot of a drain or a disposal is never completed.
    /// </remarks>
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

        lock (_subscriptionGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_draining)
            {
                subscriberChannel.Writer.TryComplete();
            }
            else
            {
                _eventSubscribers.TryAdd(subscriberId, subscriberChannel);
            }
        }

        return ReadWithCleanup<TEvent>(subscriberId, subscriberChannel.Reader, correlationId, cancellationToken);
    }

    // Yields only events of type TEvent. When correlationId is set, it also skips events whose
    // correlation ID differs, and events that do not implement ICorrelatedEvent. On completion or
    // cancellation, the finally block removes and completes the subscriber channel (M13).
    private async IAsyncEnumerable<TEvent> ReadWithCleanup<TEvent>(
        Guid subscriberId,
        ChannelReader<IOrchestratorEvent> reader,
        string? correlationId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TEvent : IOrchestratorEvent
    {
        try
        {
            await foreach (var evt in reader.ReadAllAsync(cancellationToken))
            {
                if (evt is not TEvent typedEvent)
                {
                    continue;
                }

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
                        continue;
                    }
                }

                yield return typedEvent;
            }
        }
        finally
        {
            if (_eventSubscribers.TryRemove(subscriberId, out var channel))
            {
                channel.Writer.TryComplete();
            }
        }
    }

    /// <summary>
    /// Publishes an event to all subscribers. In a builder registration, the terminal outcome
    /// stage publishes <see cref="WorkCompletedEvent{TWork}"/> events here through the builder's
    /// event callback. A handler from the public <c>CreateCompletionTrackingHandler</c> factory
    /// also publishes here.
    /// </summary>
    /// <param name="evt">The event to publish.</param>
    /// <remarks>
    /// The writes do not block, because the subscriber channels use
    /// <see cref="BoundedChannelFullMode.DropOldest"/>. After the writes, the method removes stale
    /// subscribers whose buffers are completely full (M10). A full buffer indicates that the consumer
    /// does not read events.
    /// </remarks>
    internal void PublishToSubscribers(IOrchestratorEvent evt)
    {
        if (_disposed)
        {
            return;
        }

        foreach (var subscriber in _eventSubscribers.Values)
        {
            subscriber.Writer.TryWrite(evt);
        }

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
    /// <see cref="WorkCompletedEvent{TWork}"/> events via a callback. Test seam only.
    /// </summary>
    /// <param name="innerHandler">The handler to wrap.</param>
    /// <param name="publishCallback">Callback invoked to publish completion events.</param>
    /// <returns>A handler that wraps the inner handler with completion tracking.</returns>
    /// <exception cref="ArgumentNullException">Thrown when parameters are null.</exception>
    /// <remarks>
    /// The builder no longer registers this decorator; the terminal outcome stage publishes
    /// completion events for builder registrations. Tests use this overload to observe the
    /// decorator's events without an <see cref="EventStreamOrchestrator{TWork}"/>.
    /// </remarks>
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
                await _innerHandler.HandleAsync(work, ct);
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

    /// <inheritdoc/>
    public void Complete() => _inner.Complete();

    /// <inheritdoc/>
    public Task Completion => _inner.Completion;

    /// <inheritdoc/>
    public EnqueueResult Enqueue(TWork work, WorkEnqueueOptions options = default)
    {
        var result = _inner.Enqueue(work, options);
        if (result.IsAccepted)
            PublishToSubscribers(new WorkEnqueuedEvent<TWork>(work, DateTimeOffset.UtcNow, PendingCount, options.CorrelationId));
        return result;
    }

    /// <inheritdoc/>
    public bool TryEnqueue(TWork work, WorkEnqueueOptions options = default) => Enqueue(work, options).IsAccepted;

    /// <inheritdoc/>
    public async ValueTask<EnqueueResult> EnqueueAsync(TWork work, WorkEnqueueOptions options = default, CancellationToken ct = default)
    {
        var result = await _inner.EnqueueAsync(work, options, ct);
        if (result.IsAccepted)
            PublishToSubscribers(new WorkEnqueuedEvent<TWork>(work, DateTimeOffset.UtcNow, PendingCount, options.CorrelationId));
        return result;
    }
}
