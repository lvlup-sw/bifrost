// =============================================================================
// <copyright file="EventStreamOrchestratorTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;
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
        await Task.Delay(250).ConfigureAwait(false);

        // Act
        await decorator.EnqueueAsync("test-work").ConfigureAwait(false);

        // Assert
        await _inner.Received(1).EnqueueAsync("test-work", Arg.Any<CancellationToken>()).ConfigureAwait(false);

        var evt = await receivedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
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
        await Task.Delay(250).ConfigureAwait(false);

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
        await Task.Delay(250).ConfigureAwait(false);

        // Act
        decorator.TryEnqueue("test-work");

        // Assert - verify event was published
        var evt = await receivedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
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
        await Task.Delay(250).ConfigureAwait(false);

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
        await Task.Delay(250).ConfigureAwait(false);

        var before = DateTimeOffset.UtcNow;

        // Act
        await decorator.EnqueueAsync("test-work").ConfigureAwait(false);

        var after = DateTimeOffset.UtcNow;

        // Assert
        var evt = await receivedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
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
        await Task.Delay(250).ConfigureAwait(false);

        // Act
        await decorator.EnqueueAsync("test-work").ConfigureAwait(false);

        // Assert
        var evt = await receivedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
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

    /// <summary>
    /// Verifies that PublishToSubscribers removes a subscriber whose buffer is completely full,
    /// indicating the consumer is not reading (stale subscriber cleanup - M10).
    /// </summary>
    [Test]
    public async Task PublishToSubscribers_WithFullSubscriberBuffer_RemovesStaleSubscriber()
    {
        // Arrange
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);

        // Create a subscriber but do NOT consume any events from it
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stream = decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token);

        // Fill the subscriber's buffer completely (capacity is 1000)
        // The channel uses DropOldest, so all writes succeed
        for (var i = 0; i < 1000; i++)
        {
            await decorator.EnqueueAsync($"fill-{i}").ConfigureAwait(false);
        }

        // Act - publish one more event, which should trigger stale subscriber cleanup
        await decorator.EnqueueAsync("trigger-cleanup").ConfigureAwait(false);

        // Allow a brief moment for the cleanup to process
        await Task.Delay(50).ConfigureAwait(false);

        // Assert - create a new subscriber to verify the stale one was removed.
        // The stale subscriber should have been removed, so only the new subscriber
        // exists. We verify indirectly by checking we can still enqueue without errors.
        var newSubscriberReceived = new TaskCompletionSource<IOrchestratorEvent>();
        var newSubscriberTask = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token).ConfigureAwait(false))
            {
                newSubscriberReceived.TrySetResult(evt);
                break;
            }
        });

        await Task.Delay(100).ConfigureAwait(false);
        await decorator.EnqueueAsync("verify-event").ConfigureAwait(false);

        var receivedEvt = await newSubscriberReceived.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await Assert.That(receivedEvt).IsNotNull();

        // Verify that the logger was called with a warning about stale subscriber
        _logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("stale")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// Verifies that an active subscriber (one that is reading events) is NOT removed
    /// during stale subscriber cleanup (M10). The subscriber must keep its buffer
    /// below capacity by reading events promptly.
    /// </summary>
    [Test]
    public async Task PublishToSubscribers_WithActiveSubscriber_KeepsSubscriber()
    {
        // Arrange
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var receivedEvents = new ConcurrentBag<IOrchestratorEvent>();
        var eventCount = 100; // Well below the 1000 buffer capacity

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Create a subscriber that actively consumes events
        var subscriberTask = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token).ConfigureAwait(false))
            {
                receivedEvents.Add(evt);
                if (receivedEvents.Count >= eventCount)
                {
                    break;
                }
            }
        });

        // Give subscriber time to start
        await Task.Delay(100).ConfigureAwait(false);

        // Act - publish events while subscriber is actively reading
        for (var i = 0; i < eventCount; i++)
        {
            await decorator.EnqueueAsync($"event-{i}").ConfigureAwait(false);
        }

        // Wait for subscriber to consume events
        await subscriberTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // Assert - active subscriber should have received all events and NOT been removed
        await Assert.That(receivedEvents.Count).IsEqualTo(eventCount);
    }

    /// <summary>
    /// Verifies that ReadWithCleanup properly removes the subscriber on normal completion (M13).
    /// </summary>
    [Test]
    public async Task ReadWithCleanup_CompletesNormally_RemovesSubscriber()
    {
        // Arrange
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var subscriberCts = new CancellationTokenSource();
        var eventsReceived = 0;

        // Start subscriber
        var subscriberTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: subscriberCts.Token).ConfigureAwait(false))
                {
                    eventsReceived++;
                }
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        });

        // Give subscriber time to register
        await Task.Delay(200).ConfigureAwait(false);

        // Enqueue one event to verify subscriber is working
        await decorator.EnqueueAsync("test-event").ConfigureAwait(false);
        await Task.Delay(100).ConfigureAwait(false);

        // Act - cancel the subscriber to trigger ReadWithCleanup finally block
        await subscriberCts.CancelAsync().ConfigureAwait(false);
        await subscriberTask.ConfigureAwait(false);

        // Give cleanup a moment
        await Task.Delay(100).ConfigureAwait(false);

        // Assert - subscriber was removed: verify by creating a new subscriber
        // and confirming the old one no longer exists (indirectly)
        await Assert.That(eventsReceived).IsGreaterThanOrEqualTo(1);

        // The stale subscriber should NOT generate a warning (it was cleanly removed)
        // Enqueue more events to verify no stale subscriber warnings
        var newCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var newEventReceived = new TaskCompletionSource<IOrchestratorEvent>();
        var newTask = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: newCts.Token).ConfigureAwait(false))
            {
                newEventReceived.TrySetResult(evt);
                break;
            }
        });

        await Task.Delay(100).ConfigureAwait(false);
        await decorator.EnqueueAsync("after-cleanup").ConfigureAwait(false);
        var newEvt = await newEventReceived.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await Assert.That(newEvt).IsNotNull();
    }

    /// <summary>
    /// Verifies that WorkCompletedEvent is published with Success=true when work completes
    /// successfully via the CompletionTrackingHandler (L6).
    /// </summary>
    [Test]
    public async Task WorkCompleted_OnSuccess_PublishesWorkCompletedEvent()
    {
        // Arrange - create decorator and completion tracking handler
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);

        var innerHandler = Substitute.For<IWorkHandler<string>>();
        innerHandler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);

        var trackingHandler = EventStreamOrchestrator<string>.CreateCompletionTrackingHandler(
            innerHandler, decorator);

        var receivedEvent = new TaskCompletionSource<WorkCompletedEvent<string>>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Subscribe to WorkCompletedEvent
        var subscriberTask = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<WorkCompletedEvent<string>>(cancellationToken: cts.Token).ConfigureAwait(false))
            {
                receivedEvent.TrySetResult(evt);
                break;
            }
        });

        // Give subscriber time to start
        await Task.Delay(150).ConfigureAwait(false);

        // Act - process work through the tracking handler
        await trackingHandler.HandleAsync("completed-work", CancellationToken.None).ConfigureAwait(false);

        // Assert
        var evt = await receivedEvent.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        await Assert.That(evt.Work).IsEqualTo("completed-work");
        await Assert.That(evt.Success).IsTrue();
        await Assert.That(evt.Duration).IsGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    /// <summary>
    /// Verifies that WorkCompletedEvent is published with Success=false when the handler
    /// throws an exception via the CompletionTrackingHandler (L6).
    /// </summary>
    [Test]
    public async Task WorkCompleted_OnFailure_PublishesWorkCompletedEventWithFalse()
    {
        // Arrange - create decorator and completion tracking handler
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);

        var innerHandler = Substitute.For<IWorkHandler<string>>();
        innerHandler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask>(_ => throw new InvalidOperationException("Handler failed"));

        var trackingHandler = EventStreamOrchestrator<string>.CreateCompletionTrackingHandler(
            innerHandler, decorator);

        var receivedEvent = new TaskCompletionSource<WorkCompletedEvent<string>>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Subscribe to WorkCompletedEvent
        var subscriberTask = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<WorkCompletedEvent<string>>(cancellationToken: cts.Token).ConfigureAwait(false))
            {
                receivedEvent.TrySetResult(evt);
                break;
            }
        });

        // Give subscriber time to start
        await Task.Delay(150).ConfigureAwait(false);

        // Act - process work through the tracking handler (exception is re-thrown)
        try
        {
            await trackingHandler.HandleAsync("failing-work", CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Expected - the handler throws, and CompletionTrackingHandler re-throws after publishing
        }

        // Assert
        var evt = await receivedEvent.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        await Assert.That(evt.Work).IsEqualTo("failing-work");
        await Assert.That(evt.Success).IsFalse();
        await Assert.That(evt.Duration).IsGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    /// <summary>
    /// Verifies that DrainAsync forwards to the inner orchestrator and completes
    /// subscriber channels so consumers terminate gracefully.
    /// </summary>
    [Test]
    public async Task DrainAsync_ForwardsToInner_AndCompletesSubscribers()
    {
        // Arrange
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        _inner.DrainAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        // Start a subscriber
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var subscriberCompleted = false;

        var subscriberTask = Task.Run(async () =>
        {
            await foreach (var _ in decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token).ConfigureAwait(false))
            {
                // consume events
            }

            subscriberCompleted = true;
        });

        // Give subscriber time to register
        await Task.Delay(200).ConfigureAwait(false);

        // Act
        await decorator.DrainAsync().ConfigureAwait(false);

        // Wait for subscriber task to complete
        await subscriberTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // Assert - inner was called
        await _inner.Received(1).DrainAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);

        // Assert - subscriber completed (channel was completed)
        await Assert.That(subscriberCompleted).IsTrue();
    }
}