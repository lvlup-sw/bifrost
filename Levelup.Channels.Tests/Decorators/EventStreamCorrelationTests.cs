// =============================================================================
// <copyright file="EventStreamCorrelationTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;
using Levelup.Channels.Core;
using Levelup.Channels.Core.Events;
using Levelup.Channels.Decorators;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Levelup.Channels.Tests.Decorators;

/// <summary>
/// Tests for correlation ID filtering in <see cref="EventStreamOrchestrator{TWork}"/>.
/// </summary>
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

        // Configure inner mock defaults
        _inner.PendingCount.Returns(5);
        _inner.ActiveWorkers.Returns(2);
        _inner.Capacity.Returns(100);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies that null correlationId returns all events.
    /// </summary>
    [Test]
    public async Task GetEventStreamAsync_NullCorrelationId_ReturnsAllEvents()
    {
        // Arrange
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

        // Give subscriber time to start
        await Task.Delay(100).ConfigureAwait(false);

        // Act - enqueue multiple events
        await decorator.EnqueueAsync("test1").ConfigureAwait(false);
        await decorator.EnqueueAsync("test2").ConfigureAwait(false);
        await decorator.EnqueueAsync("test3").ConfigureAwait(false);

        await subscriberTask.ConfigureAwait(false);

        // Assert - all events should be received
        await Assert.That(receivedEvents.Count).IsEqualTo(3);
    }

    /// <summary>
    /// Verifies that specified correlationId filters events correctly.
    /// </summary>
    [Test]
    public async Task GetEventStreamAsync_SpecifiedCorrelationId_FiltersCorrectly()
    {
        // Arrange
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
                // Expected if timeout occurs before we receive all events
            }
        });

        // Give subscriber time to start
        await Task.Delay(150).ConfigureAwait(false);

        // Act - enqueue events with different correlation IDs
        await decorator.EnqueueAsync("test1", targetCorrelationId).ConfigureAwait(false);
        await Task.Delay(20).ConfigureAwait(false);
        await decorator.EnqueueAsync("test2", "different-correlation").ConfigureAwait(false);
        await Task.Delay(20).ConfigureAwait(false);
        await decorator.EnqueueAsync("test3", targetCorrelationId).ConfigureAwait(false);
        await Task.Delay(20).ConfigureAwait(false);
        await decorator.EnqueueAsync("test4", "another-correlation").ConfigureAwait(false);

        await subscriberTask.ConfigureAwait(false);

        // Assert - only events with matching correlationId should be received
        await Assert.That(receivedEvents.Count).IsEqualTo(2);
        foreach (var evt in receivedEvents)
        {
            await Assert.That(evt.CorrelationId).IsEqualTo(targetCorrelationId);
        }
    }

    /// <summary>
    /// Verifies that correlation ID comparison is case-sensitive (Ordinal).
    /// </summary>
    [Test]
    public async Task GetEventStreamAsync_CorrelationIdComparison_IsCaseSensitive()
    {
        // Arrange
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
                // Expected if no matching events
            }
        });

        // Give subscriber time to start
        await Task.Delay(100).ConfigureAwait(false);

        // Act - enqueue events with different case correlation IDs
        await decorator.EnqueueAsync("test1", "correlation-abc").ConfigureAwait(false);  // lowercase
        await decorator.EnqueueAsync("test2", "CORRELATION-ABC").ConfigureAwait(false);  // uppercase
        await decorator.EnqueueAsync("test3", "Correlation-ABC").ConfigureAwait(false);  // exact match

        await subscriberTask.ConfigureAwait(false);

        // Assert - only exact case match should be received
        await Assert.That(receivedEvents.Count).IsEqualTo(1);
        await Assert.That(receivedEvents.First().CorrelationId).IsEqualTo(targetCorrelationId);
    }

    /// <summary>
    /// Verifies that events without correlation ID are excluded when filtering by correlation ID.
    /// </summary>
    [Test]
    public async Task GetEventStreamAsync_EventsWithoutCorrelationId_AreExcludedWhenFiltering()
    {
        // Arrange
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
                // Expected
            }
        });

        // Give subscriber time to start
        await Task.Delay(100).ConfigureAwait(false);

        // Act - enqueue events without correlation ID (null)
        await decorator.EnqueueAsync("test1", (string?)null).ConfigureAwait(false);
        await decorator.EnqueueAsync("test2", (string?)null).ConfigureAwait(false);

        await subscriberTask.ConfigureAwait(false);

        // Assert - no events should be received (none have matching correlationId)
        await Assert.That(receivedEvents.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies that ICorrelatedEvent interface is properly implemented.
    /// </summary>
    [Test]
    public async Task ICorrelatedEvent_HasCorrelationIdProperty()
    {
        // Assert - verify interface exists and has correct property
        var interfaceType = typeof(ICorrelatedEvent);
        var property = interfaceType.GetProperty(nameof(ICorrelatedEvent.CorrelationId));

        await Assert.That(property).IsNotNull();
        await Assert.That(property!.PropertyType).IsEqualTo(typeof(string));
    }

    /// <summary>
    /// Verifies that correlation ID filtering works with typed event filtering.
    /// </summary>
    [Test]
    public async Task GetEventStreamAsync_TypeAndCorrelationFiltering_WorkTogether()
    {
        // Arrange
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
                // Expected
            }
        });

        // Give subscriber time to start
        await Task.Delay(100).ConfigureAwait(false);

        // Act
        await decorator.EnqueueAsync("test1", targetCorrelationId).ConfigureAwait(false);

        await subscriberTask.ConfigureAwait(false);

        // Assert
        await Assert.That(receivedEvents.Count).IsEqualTo(1);
        await Assert.That(receivedEvents.First().Work).IsEqualTo("test1");
    }

    /// <summary>
    /// Verifies that EnqueueAsync overload with correlationId is available.
    /// </summary>
    [Test]
    public async Task EnqueueAsync_WithCorrelationId_IsAvailable()
    {
        // Arrange
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

        // Give subscriber time to start
        await Task.Delay(100).ConfigureAwait(false);

        // Act
        await decorator.EnqueueAsync("test-work", "my-correlation-id").ConfigureAwait(false);

        var evt = await receivedEvent.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        // Assert
        await Assert.That(evt.CorrelationId).IsEqualTo("my-correlation-id");
    }

    /// <summary>
    /// Verifies that TryEnqueue overload with correlationId is available.
    /// </summary>
    [Test]
    public async Task TryEnqueue_WithCorrelationId_IsAvailable()
    {
        // Arrange
        _inner.TryEnqueue(Arg.Any<string>()).Returns(true);
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

        // Give subscriber time to start
        await Task.Delay(100).ConfigureAwait(false);

        // Act
        var result = decorator.TryEnqueue("test-work", "my-correlation-id");

        var evt = await receivedEvent.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        // Assert
        await Assert.That(result).IsTrue();
        await Assert.That(evt.CorrelationId).IsEqualTo("my-correlation-id");
    }

    /// <summary>
    /// Verifies that events enqueued with the original EnqueueAsync (without correlationId) have null CorrelationId.
    /// </summary>
    [Test]
    public async Task EnqueueAsync_WithoutCorrelationId_EventHasNullCorrelationId()
    {
        // Arrange
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

        // Give subscriber time to start
        await Task.Delay(100).ConfigureAwait(false);

        // Act - use original EnqueueAsync without correlationId
        await decorator.EnqueueAsync("test-work").ConfigureAwait(false);

        var evt = await receivedEvent.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        // Assert - event should have null correlationId
        await Assert.That(evt.CorrelationId).IsNull();
    }
}
