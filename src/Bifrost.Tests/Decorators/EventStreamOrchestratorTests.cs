// =============================================================================
// <copyright file="EventStreamOrchestratorTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Threading.Channels;
using Bifrost.Core;
using Bifrost.Core.Events;
using Bifrost.Decorators;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Bifrost.Tests.Decorators;

/// <summary>
/// Tests for <see cref="EventStreamOrchestrator{TWork}"/> decorator.
/// </summary>
public class EventStreamOrchestratorTests
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
    /// Verifies that EnqueueAsync delegates to inner orchestrator and publishes event.
    /// </summary>
    [Test]
    public async Task EventStreamOrchestrator_EnqueueAsync_DelegatesToInnerAndPublishesEvent()
    {
        // Arrange
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var receivedEvent = new TaskCompletionSource<WorkEnqueuedEvent<string>>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Start subscriber BEFORE enqueuing (broadcast pattern requires this)
        var subscriberTask = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<WorkEnqueuedEvent<string>>(cancellationToken: cts.Token).ConfigureAwait(false))
            {
                receivedEvent.TrySetResult(evt);
                break;
            }
        });

        // Give subscriber time to start
        await Task.Delay(100).ConfigureAwait(false);

        // Act
        await decorator.EnqueueAsync("test-work").ConfigureAwait(false);

        // Assert
        await _inner.Received(1).EnqueueAsync("test-work", Arg.Any<CancellationToken>()).ConfigureAwait(false);

        var evt = await receivedEvent.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        await Assert.That(evt.Work).IsEqualTo("test-work");
    }

    /// <summary>
    /// Verifies that GetEventStreamAsync returns filtered events by type.
    /// </summary>
    [Test]
    public async Task GetEventStreamAsync_ReturnsFilteredEvents()
    {
        // Arrange
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var receivedEvents = new List<WorkEnqueuedEvent<string>>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Start subscriber BEFORE enqueuing
        var subscriberTask = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<WorkEnqueuedEvent<string>>(cancellationToken: cts.Token).ConfigureAwait(false))
            {
                receivedEvents.Add(evt);
                if (receivedEvents.Count >= 2)
                {
                    break;
                }
            }
        });

        // Give subscriber time to start
        await Task.Delay(100).ConfigureAwait(false);

        // Act - enqueue work to generate events
        await decorator.EnqueueAsync("test1").ConfigureAwait(false);
        await decorator.EnqueueAsync("test2").ConfigureAwait(false);

        await subscriberTask.ConfigureAwait(false);

        // Assert - only WorkEnqueuedEvent should be returned
        await Assert.That(receivedEvents.Count).IsEqualTo(2);
    }

    /// <summary>
    /// Verifies that PendingCount delegates to inner orchestrator.
    /// </summary>
    [Test]
    public async Task PendingCount_DelegatesToInner()
    {
        // Arrange
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);

        // Act
        var result = decorator.PendingCount;

        // Assert
        await Assert.That(result).IsEqualTo(5);
    }

    /// <summary>
    /// Verifies that ActiveWorkers delegates to inner orchestrator.
    /// </summary>
    [Test]
    public async Task ActiveWorkers_DelegatesToInner()
    {
        // Arrange
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);

        // Act
        var result = decorator.ActiveWorkers;

        // Assert
        await Assert.That(result).IsEqualTo(2);
    }

    /// <summary>
    /// Verifies that Capacity delegates to inner orchestrator.
    /// </summary>
    [Test]
    public async Task Capacity_DelegatesToInner()
    {
        // Arrange
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);

        // Act
        var result = decorator.Capacity;

        // Assert
        await Assert.That(result).IsEqualTo(100);
    }

    /// <summary>
    /// Verifies that TryEnqueue delegates to inner orchestrator.
    /// </summary>
    [Test]
    public async Task TryEnqueue_DelegatesToInner()
    {
        // Arrange
        _inner.TryEnqueue(Arg.Any<string>()).Returns(true);
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);

        // Act
        var result = decorator.TryEnqueue("test-work");

        // Assert
        await Assert.That(result).IsTrue();
        _inner.Received(1).TryEnqueue("test-work");
    }

    /// <summary>
    /// Verifies that TryEnqueue publishes event when successful.
    /// </summary>
    [Test]
    public async Task TryEnqueue_WhenSuccessful_PublishesEvent()
    {
        // Arrange
        _inner.TryEnqueue(Arg.Any<string>()).Returns(true);
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var receivedEvent = new TaskCompletionSource<WorkEnqueuedEvent<string>>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Start subscriber BEFORE enqueuing
        var subscriberTask = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<WorkEnqueuedEvent<string>>(cancellationToken: cts.Token).ConfigureAwait(false))
            {
                receivedEvent.TrySetResult(evt);
                break;
            }
        });

        // Give subscriber time to start
        await Task.Delay(100).ConfigureAwait(false);

        // Act
        decorator.TryEnqueue("test-work");

        // Assert - verify event was published
        var evt = await receivedEvent.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        await Assert.That(evt.Work).IsEqualTo("test-work");
    }

    /// <summary>
    /// Verifies that TryEnqueue does not publish event when it fails.
    /// </summary>
    [Test]
    public async Task TryEnqueue_WhenFails_DoesNotPublishEvent()
    {
        // Arrange
        _inner.TryEnqueue(Arg.Any<string>()).Returns(false);
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var eventReceived = false;

        // Start subscriber BEFORE enqueuing
        var subscriberTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in decorator.GetEventStreamAsync<WorkEnqueuedEvent<string>>(cancellationToken: cts.Token).ConfigureAwait(false))
                {
                    eventReceived = true;
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
        var result = decorator.TryEnqueue("test-work");

        // Wait for timeout
        await subscriberTask.ConfigureAwait(false);

        // Assert
        await Assert.That(result).IsFalse();
        await Assert.That(eventReceived).IsFalse();
    }

    /// <summary>
    /// Verifies that StopAsync delegates to inner orchestrator.
    /// </summary>
    [Test]
    public async Task StopAsync_DelegatesToInner()
    {
        // Arrange
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);

        // Act
        await decorator.StopAsync().ConfigureAwait(false);

        // Assert
        await _inner.Received(1).StopAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that DisposeAsync delegates to inner orchestrator.
    /// </summary>
    [Test]
    public async Task DisposeAsync_DelegatesToInner()
    {
        // Arrange
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);

        // Act
        await decorator.DisposeAsync().ConfigureAwait(false);

        // Assert
        await _inner.Received(1).DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that Writer delegates to inner orchestrator.
    /// </summary>
    [Test]
    public async Task Writer_DelegatesToInner()
    {
        // Arrange
        var channel = Channel.CreateBounded<string>(10);
        _inner.Writer.Returns(channel.Writer);
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);

        // Act
        var result = decorator.Writer;

        // Assert
        await Assert.That(result).IsSameReferenceAs(channel.Writer);
    }

    /// <summary>
    /// Verifies that EventStreamOrchestrator implements IEventStreamOrchestrator.
    /// </summary>
    [Test]
    public async Task EventStreamOrchestrator_ImplementsIEventStreamOrchestrator()
    {
        // Arrange
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);

        // Assert
        await Assert.That(decorator).IsAssignableTo<IEventStreamOrchestrator<string>>();
    }

    /// <summary>
    /// Verifies that constructor throws ArgumentNullException for null inner.
    /// </summary>
    [Test]
    public async Task Constructor_NullInner_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.That(() => new EventStreamOrchestrator<string>(null!, _logger))
            .ThrowsExactly<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that constructor throws ArgumentNullException for null logger.
    /// </summary>
    [Test]
    public async Task Constructor_NullLogger_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.That(() => new EventStreamOrchestrator<string>(_inner, null!))
            .ThrowsExactly<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that event includes correct timestamp.
    /// </summary>
    [Test]
    public async Task EnqueueAsync_EventIncludesTimestamp()
    {
        // Arrange
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var receivedEvent = new TaskCompletionSource<WorkEnqueuedEvent<string>>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Start subscriber BEFORE enqueuing
        var subscriberTask = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<WorkEnqueuedEvent<string>>(cancellationToken: cts.Token).ConfigureAwait(false))
            {
                receivedEvent.TrySetResult(evt);
                break;
            }
        });

        // Give subscriber time to start
        await Task.Delay(100).ConfigureAwait(false);

        var before = DateTimeOffset.UtcNow;

        // Act
        await decorator.EnqueueAsync("test-work").ConfigureAwait(false);

        var after = DateTimeOffset.UtcNow;

        // Assert
        var evt = await receivedEvent.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        await Assert.That(evt.Timestamp).IsGreaterThanOrEqualTo(before);
        await Assert.That(evt.Timestamp).IsLessThanOrEqualTo(after);
    }

    /// <summary>
    /// Verifies that event includes correct queue depth.
    /// </summary>
    [Test]
    public async Task EnqueueAsync_EventIncludesQueueDepth()
    {
        // Arrange
        _inner.PendingCount.Returns(10);
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var receivedEvent = new TaskCompletionSource<WorkEnqueuedEvent<string>>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Start subscriber BEFORE enqueuing
        var subscriberTask = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<WorkEnqueuedEvent<string>>(cancellationToken: cts.Token).ConfigureAwait(false))
            {
                receivedEvent.TrySetResult(evt);
                break;
            }
        });

        // Give subscriber time to start
        await Task.Delay(100).ConfigureAwait(false);

        // Act
        await decorator.EnqueueAsync("test-work").ConfigureAwait(false);

        // Assert
        var evt = await receivedEvent.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        await Assert.That(evt.QueueDepth).IsEqualTo(10);
    }

    /// <summary>
    /// Verifies that DisposeAsync completes the event channel so consumers don't block indefinitely.
    /// </summary>
    [Test]
    public async Task DisposeAsync_CompletesEventChannel_ConsumersTerminateGracefully()
    {
        // Arrange
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);

        // Start consuming events in background task
        var eventConsumerTask = Task.Run(async () =>
        {
            var eventCount = 0;
            await foreach (var _ in decorator.GetEventStreamAsync<WorkEnqueuedEvent<string>>().ConfigureAwait(false))
            {
                eventCount++;
            }

            return eventCount;
        });

        // Give consumer time to start waiting
        await Task.Delay(50).ConfigureAwait(false);

        // Act - dispose the orchestrator
        await decorator.DisposeAsync().ConfigureAwait(false);

        // Assert - consumer task should complete (not block indefinitely)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var completedSuccessfully = await Task.WhenAny(eventConsumerTask, Task.Delay(Timeout.Infinite, cts.Token)).ConfigureAwait(false) == eventConsumerTask;

        await Assert.That(completedSuccessfully).IsTrue();
        await _inner.Received(1).DisposeAsync().ConfigureAwait(false);
    }
}
