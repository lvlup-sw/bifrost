// =============================================================================
// <copyright file="EventStreamCorrelationTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;

using Bifrost.Core;
using Bifrost.Core.Events;
using Bifrost.Decorators;

using Microsoft.Extensions.Logging;

using NSubstitute;

namespace Bifrost.Tests.Decorators;

/// <summary>
/// Tests for correlation ID filtering in <see cref="EventStreamOrchestrator{TWork}"/>.
/// </summary>
[ParallelLimiter<EventStreamParallelLimit>]
public class EventStreamCorrelationTests
{
    private IWorkOrchestrator<string> _inner = null!;
    private ILogger<EventStreamOrchestrator<string>> _logger = null!;

    /// <summary>
    /// Sets up test fixtures before each test.
    /// </summary>
    [Before(Test)]
    public Task Setup()
    {
        _inner = Substitute.For<IWorkOrchestrator<string>>();
        _logger = Substitute.For<ILogger<EventStreamOrchestrator<string>>>();

        _inner.PendingCount.Returns(5);
        _inner.ActiveWorkers.Returns(2);
        _inner.Capacity.Returns(100);
        _inner.EnqueueAsync(Arg.Any<string>(), Arg.Any<WorkEnqueueOptions>(), Arg.Any<CancellationToken>())
            .Returns(EnqueueResult.Accepted);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies that null correlationId returns all events.
    /// </summary>
    /// <remarks>
    /// The delay gives the subscriber time to start. The subscriber must receive all the enqueued
    /// events.
    /// </remarks>
    [Test]
    public async Task GetEventStreamAsync_NullCorrelationId_ReturnsAllEvents()
    {
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var receivedEvents = new ConcurrentBag<IOrchestratorEvent>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var subscriberTask = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<IOrchestratorEvent>(correlationId: null, cancellationToken: cts.Token).ConfigureAwait(false))
            {
                receivedEvents.Add(evt);
                if (receivedEvents.Count >= 3)
                {
                    break;
                }
            }
        });

        await Task.Delay(100).ConfigureAwait(false);

        await decorator.EnqueueAsync("test1").ConfigureAwait(false);
        await decorator.EnqueueAsync("test2").ConfigureAwait(false);
        await decorator.EnqueueAsync("test3").ConfigureAwait(false);

        await subscriberTask.ConfigureAwait(false);

        await Assert.That(receivedEvents.Count).IsEqualTo(3);
    }

    /// <summary>
    /// Verifies that specified correlationId filters events correctly.
    /// </summary>
    /// <remarks>
    /// The <c>OperationCanceledException</c> is expected if the timeout occurs before the
    /// subscriber receives all events. The test enqueues events with different correlation IDs. The
    /// subscriber must receive only the events with the matching correlation ID.
    /// </remarks>
    [Test]
    public async Task GetEventStreamAsync_SpecifiedCorrelationId_FiltersCorrectly()
    {
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var receivedEvents = new ConcurrentBag<ICorrelatedEvent>();
        var targetCorrelationId = "correlation-123";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var subscriberTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in decorator.GetEventStreamAsync<ICorrelatedEvent>(correlationId: targetCorrelationId, cancellationToken: cts.Token).ConfigureAwait(false))
                {
                    receivedEvents.Add(evt);
                    if (receivedEvents.Count >= 2)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        await Task.Delay(150).ConfigureAwait(false);

        await decorator.EnqueueAsync("test1", new WorkEnqueueOptions(CorrelationId: targetCorrelationId)).ConfigureAwait(false);
        await Task.Delay(20).ConfigureAwait(false);
        await decorator.EnqueueAsync("test2", new WorkEnqueueOptions(CorrelationId: "different-correlation")).ConfigureAwait(false);
        await Task.Delay(20).ConfigureAwait(false);
        await decorator.EnqueueAsync("test3", new WorkEnqueueOptions(CorrelationId: targetCorrelationId)).ConfigureAwait(false);
        await Task.Delay(20).ConfigureAwait(false);
        await decorator.EnqueueAsync("test4", new WorkEnqueueOptions(CorrelationId: "another-correlation")).ConfigureAwait(false);

        await subscriberTask.ConfigureAwait(false);

        await Assert.That(receivedEvents.Count).IsEqualTo(2);
        foreach (var evt in receivedEvents)
        {
            await Assert.That(evt.CorrelationId).IsEqualTo(targetCorrelationId);
        }
    }

    /// <summary>
    /// Verifies that correlation ID comparison is case-sensitive (Ordinal).
    /// </summary>
    /// <remarks>
    /// The <c>OperationCanceledException</c> is expected if no matching events arrive. The test
    /// enqueues "correlation-abc" (lowercase), "CORRELATION-ABC" (uppercase), and "Correlation-ABC"
    /// (exact match). The subscriber must receive only the exact case match.
    /// </remarks>
    [Test]
    public async Task GetEventStreamAsync_CorrelationIdComparison_IsCaseSensitive()
    {
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var receivedEvents = new ConcurrentBag<ICorrelatedEvent>();
        var targetCorrelationId = "Correlation-ABC";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var subscriberTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in decorator.GetEventStreamAsync<ICorrelatedEvent>(correlationId: targetCorrelationId, cancellationToken: cts.Token).ConfigureAwait(false))
                {
                    receivedEvents.Add(evt);
                    if (receivedEvents.Count >= 1)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        await Task.Delay(100).ConfigureAwait(false);

        await decorator.EnqueueAsync("test1", new WorkEnqueueOptions(CorrelationId: "correlation-abc")).ConfigureAwait(false);
        await decorator.EnqueueAsync("test2", new WorkEnqueueOptions(CorrelationId: "CORRELATION-ABC")).ConfigureAwait(false);
        await decorator.EnqueueAsync("test3", new WorkEnqueueOptions(CorrelationId: "Correlation-ABC")).ConfigureAwait(false);

        await subscriberTask.ConfigureAwait(false);

        await Assert.That(receivedEvents.Count).IsEqualTo(1);
        await Assert.That(receivedEvents.First().CorrelationId).IsEqualTo(targetCorrelationId);
    }

    /// <summary>
    /// Verifies that events without correlation ID are excluded when filtering by correlation ID.
    /// </summary>
    /// <remarks>
    /// The <c>OperationCanceledException</c> is expected. The test enqueues events with a null
    /// correlation ID. None of them has the matching correlation ID, so the subscriber must receive
    /// no events.
    /// </remarks>
    [Test]
    public async Task GetEventStreamAsync_EventsWithoutCorrelationId_AreExcludedWhenFiltering()
    {
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var receivedEvents = new ConcurrentBag<ICorrelatedEvent>();
        var targetCorrelationId = "correlation-filter";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var subscriberTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in decorator.GetEventStreamAsync<ICorrelatedEvent>(correlationId: targetCorrelationId, cancellationToken: cts.Token).ConfigureAwait(false))
                {
                    receivedEvents.Add(evt);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        await Task.Delay(100).ConfigureAwait(false);

        await decorator.EnqueueAsync("test1", new WorkEnqueueOptions(CorrelationId: null)).ConfigureAwait(false);
        await decorator.EnqueueAsync("test2", new WorkEnqueueOptions(CorrelationId: null)).ConfigureAwait(false);

        await subscriberTask.ConfigureAwait(false);

        await Assert.That(receivedEvents.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies that ICorrelatedEvent interface is properly implemented.
    /// </summary>
    [Test]
    public async Task ICorrelatedEvent_HasCorrelationIdProperty()
    {
        var interfaceType = typeof(ICorrelatedEvent);
        var property = interfaceType.GetProperty(nameof(ICorrelatedEvent.CorrelationId));

        await Assert.That(property).IsNotNull();
        await Assert.That(property!.PropertyType).IsEqualTo(typeof(string));
    }

    /// <summary>
    /// Verifies that correlation ID filtering works with typed event filtering.
    /// </summary>
    /// <remarks>
    /// The <c>OperationCanceledException</c> is expected.
    /// </remarks>
    [Test]
    public async Task GetEventStreamAsync_TypeAndCorrelationFiltering_WorkTogether()
    {
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var receivedEvents = new ConcurrentBag<WorkEnqueuedEvent<string>>();
        var targetCorrelationId = "workflow-123";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var subscriberTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in decorator.GetEventStreamAsync<WorkEnqueuedEvent<string>>(correlationId: targetCorrelationId, cancellationToken: cts.Token).ConfigureAwait(false))
                {
                    receivedEvents.Add(evt);
                    if (receivedEvents.Count >= 1)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        await Task.Delay(100).ConfigureAwait(false);

        await decorator.EnqueueAsync("test1", new WorkEnqueueOptions(CorrelationId: targetCorrelationId)).ConfigureAwait(false);

        await subscriberTask.ConfigureAwait(false);

        await Assert.That(receivedEvents.Count).IsEqualTo(1);
        await Assert.That(receivedEvents.First().Work).IsEqualTo("test1");
    }

    /// <summary>
    /// Verifies that EnqueueAsync overload with correlationId is available.
    /// </summary>
    [Test]
    public async Task EnqueueAsync_WithCorrelationId_IsAvailable()
    {
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var receivedEvent = new TaskCompletionSource<ICorrelatedEvent>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var subscriberTask = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<ICorrelatedEvent>(cancellationToken: cts.Token).ConfigureAwait(false))
            {
                receivedEvent.TrySetResult(evt);
                break;
            }
        });

        await Task.Delay(100).ConfigureAwait(false);

        await decorator.EnqueueAsync("test-work", new WorkEnqueueOptions(CorrelationId: "my-correlation-id")).ConfigureAwait(false);

        var evt = await receivedEvent.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        await Assert.That(evt.CorrelationId).IsEqualTo("my-correlation-id");
    }

    /// <summary>
    /// Verifies that TryEnqueue overload with correlationId is available.
    /// </summary>
    [Test]
    public async Task TryEnqueue_WithCorrelationId_IsAvailable()
    {
        _inner.Enqueue(Arg.Any<string>(), Arg.Any<WorkEnqueueOptions>()).Returns(EnqueueResult.Accepted);
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var receivedEvent = new TaskCompletionSource<ICorrelatedEvent>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var subscriberTask = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<ICorrelatedEvent>(cancellationToken: cts.Token).ConfigureAwait(false))
            {
                receivedEvent.TrySetResult(evt);
                break;
            }
        });

        await Task.Delay(100).ConfigureAwait(false);

        var result = decorator.TryEnqueue("test-work", new WorkEnqueueOptions(CorrelationId: "my-correlation-id"));

        var evt = await receivedEvent.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        await Assert.That(result).IsTrue();
        await Assert.That(evt.CorrelationId).IsEqualTo("my-correlation-id");
    }

    /// <summary>
    /// Verifies that events enqueued with the original EnqueueAsync (without correlationId) have null CorrelationId.
    /// </summary>
    /// <remarks>
    /// The test uses the original <c>EnqueueAsync</c> without a correlation ID, so the event must
    /// have a null correlation ID.
    /// </remarks>
    [Test]
    public async Task EnqueueAsync_WithoutCorrelationId_EventHasNullCorrelationId()
    {
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var receivedEvent = new TaskCompletionSource<ICorrelatedEvent>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var subscriberTask = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<ICorrelatedEvent>(cancellationToken: cts.Token).ConfigureAwait(false))
            {
                receivedEvent.TrySetResult(evt);
                break;
            }
        });

        await Task.Delay(100).ConfigureAwait(false);

        await decorator.EnqueueAsync("test-work").ConfigureAwait(false);

        var evt = await receivedEvent.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        await Assert.That(evt.CorrelationId).IsNull();
    }
}
