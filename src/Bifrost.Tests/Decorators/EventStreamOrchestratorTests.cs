// =============================================================================
// <copyright file="EventStreamOrchestratorTests.cs" company="Levelup Software">
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
/// Tests for <see cref="EventStreamOrchestrator{TWork}"/> decorator.
/// </summary>
[ParallelLimiter<EventStreamParallelLimit>]
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

        _inner.PendingCount.Returns(5);
        _inner.ActiveWorkers.Returns(2);
        _inner.Capacity.Returns(100);
        _inner.EnqueueAsync(Arg.Any<string>(), Arg.Any<WorkEnqueueOptions>(), Arg.Any<CancellationToken>())
            .Returns(EnqueueResult.Accepted);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies that EnqueueAsync delegates to inner orchestrator and publishes event.
    /// </summary>
    /// <remarks>
    /// The subscriber must start BEFORE the enqueue, because the broadcast pattern requires it.
    /// </remarks>
    [Test]
    public async Task EventStreamOrchestrator_EnqueueAsync_DelegatesToInnerAndPublishesEvent()
    {
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var receivedEvent = new TaskCompletionSource<WorkEnqueuedEvent<string>>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var subscriberTask = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<WorkEnqueuedEvent<string>>(cancellationToken: cts.Token))
            {
                receivedEvent.TrySetResult(evt);
                break;
            }
        });

        await Task.Delay(250);

        await decorator.EnqueueAsync("test-work");

        await _inner.Received(1).EnqueueAsync("test-work", Arg.Any<WorkEnqueueOptions>(), Arg.Any<CancellationToken>());

        var evt = await receivedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(evt.Work).IsEqualTo("test-work");
    }

    /// <summary>
    /// Verifies that GetEventStreamAsync returns filtered events by type.
    /// </summary>
    /// <remarks>
    /// The subscriber starts BEFORE the enqueue. Only <c>WorkEnqueuedEvent</c> instances must be
    /// returned.
    /// </remarks>
    [Test]
    public async Task GetEventStreamAsync_ReturnsFilteredEvents()
    {
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var receivedEvents = new List<WorkEnqueuedEvent<string>>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var subscriberTask = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<WorkEnqueuedEvent<string>>(cancellationToken: cts.Token))
            {
                receivedEvents.Add(evt);
                if (receivedEvents.Count >= 2)
                {
                    break;
                }
            }
        });

        await Task.Delay(250);

        await decorator.EnqueueAsync("test1");
        await decorator.EnqueueAsync("test2");

        await subscriberTask;

        await Assert.That(receivedEvents.Count).IsEqualTo(2);
    }

    /// <summary>
    /// Verifies that PendingCount delegates to inner orchestrator.
    /// </summary>
    [Test]
    public async Task PendingCount_DelegatesToInner()
    {
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);

        var result = decorator.PendingCount;

        await Assert.That(result).IsEqualTo(5);
    }

    /// <summary>
    /// Verifies that ActiveWorkers delegates to inner orchestrator.
    /// </summary>
    [Test]
    public async Task ActiveWorkers_DelegatesToInner()
    {
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);

        var result = decorator.ActiveWorkers;

        await Assert.That(result).IsEqualTo(2);
    }

    /// <summary>
    /// Verifies that Capacity delegates to inner orchestrator.
    /// </summary>
    [Test]
    public async Task Capacity_DelegatesToInner()
    {
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);

        var result = decorator.Capacity;

        await Assert.That(result).IsEqualTo(100);
    }

    /// <summary>
    /// Verifies that TryEnqueue delegates to inner orchestrator.
    /// </summary>
    [Test]
    public async Task TryEnqueue_DelegatesToInner()
    {
        _inner.Enqueue(Arg.Any<string>()).Returns(EnqueueResult.Accepted);
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);

        var result = decorator.TryEnqueue("test-work");

        await Assert.That(result).IsTrue();
        _inner.Received(1).Enqueue("test-work");
    }

    /// <summary>
    /// Verifies that TryEnqueue publishes event when successful.
    /// </summary>
    /// <remarks>
    /// The subscriber starts BEFORE the enqueue.
    /// </remarks>
    [Test]
    public async Task TryEnqueue_WhenSuccessful_PublishesEvent()
    {
        _inner.Enqueue(Arg.Any<string>()).Returns(EnqueueResult.Accepted);
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var receivedEvent = new TaskCompletionSource<WorkEnqueuedEvent<string>>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var subscriberTask = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<WorkEnqueuedEvent<string>>(cancellationToken: cts.Token))
            {
                receivedEvent.TrySetResult(evt);
                break;
            }
        });

        await Task.Delay(250);

        decorator.TryEnqueue("test-work");

        var evt = await receivedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(evt.Work).IsEqualTo("test-work");
    }

    /// <summary>
    /// Verifies that TryEnqueue does not publish event when it fails.
    /// </summary>
    /// <remarks>
    /// The subscriber starts BEFORE the enqueue. The <c>OperationCanceledException</c> at the
    /// timeout is expected.
    /// </remarks>
    [Test]
    public async Task TryEnqueue_WhenFails_DoesNotPublishEvent()
    {
        _inner.Enqueue(Arg.Any<string>()).Returns(EnqueueResult.Rejected(RejectionReason.CapacityExceeded));
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var eventReceived = false;

        var subscriberTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in decorator.GetEventStreamAsync<WorkEnqueuedEvent<string>>(cancellationToken: cts.Token))
                {
                    eventReceived = true;
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        await Task.Delay(250);

        var result = decorator.TryEnqueue("test-work");

        await subscriberTask;

        await Assert.That(result).IsFalse();
        await Assert.That(eventReceived).IsFalse();
    }

    /// <summary>
    /// Verifies that StopAsync delegates to inner orchestrator.
    /// </summary>
    [Test]
    public async Task StopAsync_DelegatesToInner()
    {
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);

        await decorator.AbortAsync();

        await _inner.Received(1).AbortAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies that DisposeAsync delegates to inner orchestrator.
    /// </summary>
    [Test]
    public async Task DisposeAsync_DelegatesToInner()
    {
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);

        await decorator.DisposeAsync();

        await _inner.Received(1).DisposeAsync();
    }

    /// <summary>
    /// Verifies that a rejected admission outcome suppresses event publication.
    /// </summary>
    /// <remarks>
    /// The <c>OperationCanceledException</c> is expected when no event arrives before the timeout.
    /// </remarks>
    [Test]
    public async Task EnqueueAsync_Rejected_DoesNotPublishEvent()
    {
        _inner.EnqueueAsync(Arg.Any<string>(), Arg.Any<WorkEnqueueOptions>(), Arg.Any<CancellationToken>())
            .Returns(EnqueueResult.Rejected(RejectionReason.Shutdown));
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var eventReceived = false;

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var subscriberTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in decorator.GetEventStreamAsync<WorkEnqueuedEvent<string>>(cancellationToken: cts.Token))
                {
                    eventReceived = true;
                    break;
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        await Task.Delay(100);

        var result = await decorator.EnqueueAsync("rejected-work");
        await subscriberTask;

        await Assert.That(result.IsAccepted).IsFalse();
        await Assert.That(eventReceived).IsFalse();
    }

    /// <summary>
    /// Verifies that EventStreamOrchestrator implements IEventStreamOrchestrator.
    /// </summary>
    [Test]
    public async Task EventStreamOrchestrator_ImplementsIEventStreamOrchestrator()
    {
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);

        await Assert.That(decorator).IsAssignableTo<IEventStreamOrchestrator<string>>();
    }

    /// <summary>
    /// Verifies that constructor throws ArgumentNullException for null inner.
    /// </summary>
    [Test]
    public async Task Constructor_NullInner_ThrowsArgumentNullException()
    {
        await Assert.That(() => new EventStreamOrchestrator<string>(null!, _logger))
            .ThrowsExactly<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that constructor throws ArgumentNullException for null logger.
    /// </summary>
    [Test]
    public async Task Constructor_NullLogger_ThrowsArgumentNullException()
    {
        await Assert.That(() => new EventStreamOrchestrator<string>(_inner, null!))
            .ThrowsExactly<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that event includes correct timestamp.
    /// </summary>
    /// <remarks>
    /// The subscriber starts BEFORE the enqueue.
    /// </remarks>
    [Test]
    public async Task EnqueueAsync_EventIncludesTimestamp()
    {
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var receivedEvent = new TaskCompletionSource<WorkEnqueuedEvent<string>>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var subscriberTask = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<WorkEnqueuedEvent<string>>(cancellationToken: cts.Token))
            {
                receivedEvent.TrySetResult(evt);
                break;
            }
        });

        await Task.Delay(250);

        var before = DateTimeOffset.UtcNow;

        await decorator.EnqueueAsync("test-work");

        var after = DateTimeOffset.UtcNow;

        var evt = await receivedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(evt.Timestamp).IsGreaterThanOrEqualTo(before);
        await Assert.That(evt.Timestamp).IsLessThanOrEqualTo(after);
    }

    /// <summary>
    /// Verifies that event includes correct queue depth.
    /// </summary>
    /// <remarks>
    /// The subscriber starts BEFORE the enqueue.
    /// </remarks>
    [Test]
    public async Task EnqueueAsync_EventIncludesQueueDepth()
    {
        _inner.PendingCount.Returns(10);
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var receivedEvent = new TaskCompletionSource<WorkEnqueuedEvent<string>>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var subscriberTask = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<WorkEnqueuedEvent<string>>(cancellationToken: cts.Token))
            {
                receivedEvent.TrySetResult(evt);
                break;
            }
        });

        await Task.Delay(250);

        await decorator.EnqueueAsync("test-work");

        var evt = await receivedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(evt.QueueDepth).IsEqualTo(10);
    }

    /// <summary>
    /// Verifies that PublishToSubscribers removes a subscriber whose buffer is completely full,
    /// indicating the consumer is not reading (stale subscriber cleanup - M10).
    /// </summary>
    /// <remarks>
    /// The test makes a subscriber but does NOT consume any events from it. Then it fills the
    /// buffer of that subscriber completely (capacity 1000). The channel uses DropOldest, so all
    /// writes succeed. One more publish triggers the stale-subscriber cleanup, and a short delay
    /// lets the cleanup run. The test checks the removal indirectly: a new subscriber still
    /// receives an event and the enqueue has no errors. The logger must receive a warning about the
    /// stale subscriber.
    /// </remarks>
    [Test]
    public async Task PublishToSubscribers_WithFullSubscriberBuffer_RemovesStaleSubscriber()
    {
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stream = decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token);

        for (var i = 0; i < 1000; i++)
        {
            await decorator.EnqueueAsync($"fill-{i}");
        }

        await decorator.EnqueueAsync("trigger-cleanup");

        await Task.Delay(50);

        var newSubscriberReceived = new TaskCompletionSource<IOrchestratorEvent>();
        var newSubscriberTask = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token))
            {
                newSubscriberReceived.TrySetResult(evt);
                break;
            }
        });

        await Task.Delay(100);
        await decorator.EnqueueAsync("verify-event");

        var receivedEvt = await newSubscriberReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(receivedEvt).IsNotNull();

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
    /// <remarks>
    /// <c>eventCount</c> (100) is well below the buffer capacity of 1000. The subscriber consumes
    /// events actively while the test publishes. The active subscriber must receive all events and
    /// must NOT be removed.
    /// </remarks>
    [Test]
    public async Task PublishToSubscribers_WithActiveSubscriber_KeepsSubscriber()
    {
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var receivedEvents = new ConcurrentBag<IOrchestratorEvent>();
        var eventCount = 100;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var subscriberTask = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token))
            {
                receivedEvents.Add(evt);
                if (receivedEvents.Count >= eventCount)
                {
                    break;
                }
            }
        });

        await Task.Delay(100);

        for (var i = 0; i < eventCount; i++)
        {
            await decorator.EnqueueAsync($"event-{i}");
        }

        await subscriberTask.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(receivedEvents.Count).IsEqualTo(eventCount);
    }

    /// <summary>
    /// Verifies that ReadWithCleanup properly removes the subscriber on normal completion (M13).
    /// </summary>
    /// <remarks>
    /// The test registers the first subscriber synchronously before it publishes, because
    /// scheduling is not a registration barrier. The <c>OperationCanceledException</c> in the
    /// subscriber is expected. One enqueue makes sure that the subscriber works. Then the test
    /// cancels the subscriber, which triggers the <c>finally</c> block of <c>ReadWithCleanup</c>,
    /// and gives the cleanup a moment. The test checks the removal indirectly with a new subscriber
    /// and more enqueues. The cleanup removed the old subscriber, so it must NOT cause a
    /// stale-subscriber warning.
    /// </remarks>
    [Test]
    public async Task ReadWithCleanup_CompletesNormally_RemovesSubscriber()
    {
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var subscriberCts = new CancellationTokenSource();
        var eventsReceived = 0;

        var firstStream = decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: subscriberCts.Token);
        var subscriberTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in firstStream)
                {
                    eventsReceived++;
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        await Task.Delay(200);

        await decorator.EnqueueAsync("test-event");
        await Task.Delay(100);

        await subscriberCts.CancelAsync();
        await subscriberTask;

        await Task.Delay(100);

        await Assert.That(eventsReceived).IsGreaterThanOrEqualTo(1);

        var newCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var newEventReceived = new TaskCompletionSource<IOrchestratorEvent>();
        var secondStream = decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: newCts.Token);
        var newTask = Task.Run(async () =>
        {
            await foreach (var evt in secondStream)
            {
                newEventReceived.TrySetResult(evt);
                break;
            }
        });

        await Task.Delay(100);
        await decorator.EnqueueAsync("after-cleanup");
        var newEvt = await newEventReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(newEvt).IsNotNull();
    }

    /// <summary>
    /// Verifies that WorkCompletedEvent is published with Success=true when work completes
    /// successfully via the CompletionTrackingHandler (L6).
    /// </summary>
    /// <remarks>
    /// The test subscribes to <c>WorkCompletedEvent</c> and then processes work through the
    /// completion tracking handler.
    /// </remarks>
    [Test]
    public async Task WorkCompleted_OnSuccess_PublishesWorkCompletedEvent()
    {
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);

        var innerHandler = Substitute.For<IWorkHandler<string>>();
        innerHandler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);

        var trackingHandler = EventStreamOrchestrator<string>.CreateCompletionTrackingHandler(
            innerHandler, decorator);

        var receivedEvent = new TaskCompletionSource<WorkCompletedEvent<string>>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var subscriberTask = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<WorkCompletedEvent<string>>(cancellationToken: cts.Token))
            {
                receivedEvent.TrySetResult(evt);
                break;
            }
        });

        await Task.Delay(150);

        await trackingHandler.HandleAsync("completed-work", CancellationToken.None);

        var evt = await receivedEvent.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.That(evt.Work).IsEqualTo("completed-work");
        await Assert.That(evt.Success).IsTrue();
        await Assert.That(evt.Duration).IsGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    /// <summary>
    /// Verifies that WorkCompletedEvent is published with Success=false when the handler
    /// throws an exception via the CompletionTrackingHandler (L6).
    /// </summary>
    /// <remarks>
    /// The test subscribes to <c>WorkCompletedEvent</c> and then processes work through the
    /// completion tracking handler. The handler throws, and <c>CompletionTrackingHandler</c> throws
    /// again after it publishes, so the exception is expected.
    /// </remarks>
    [Test]
    public async Task WorkCompleted_OnFailure_PublishesWorkCompletedEventWithFalse()
    {
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);

        var innerHandler = Substitute.For<IWorkHandler<string>>();
        innerHandler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask>(_ => throw new InvalidOperationException("Handler failed"));

        var trackingHandler = EventStreamOrchestrator<string>.CreateCompletionTrackingHandler(
            innerHandler, decorator);

        var receivedEvent = new TaskCompletionSource<WorkCompletedEvent<string>>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var subscriberTask = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<WorkCompletedEvent<string>>(cancellationToken: cts.Token))
            {
                receivedEvent.TrySetResult(evt);
                break;
            }
        });

        await Task.Delay(150);

        try
        {
            await trackingHandler.HandleAsync("failing-work", CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
        }

        var evt = await receivedEvent.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.That(evt.Work).IsEqualTo("failing-work");
        await Assert.That(evt.Success).IsFalse();
        await Assert.That(evt.Duration).IsGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    /// <summary>
    /// Verifies that DrainAsync forwards to the inner orchestrator and completes
    /// subscriber channels so consumers terminate gracefully.
    /// </summary>
    /// <remarks>
    /// The test starts a subscriber that consumes events and gives it time to register. After the
    /// drain, the inner orchestrator must have been called, and the subscriber task must complete,
    /// because the drain completed the channel.
    /// </remarks>
    [Test]
    public async Task DrainAsync_ForwardsToInner_AndCompletesSubscribers()
    {
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        _inner.DrainAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var subscriberCompleted = false;

        var subscriberTask = Task.Run(async () =>
        {
            await foreach (var _ in decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token))
            {
            }

            subscriberCompleted = true;
        });

        await Task.Delay(200);

        await decorator.DrainAsync();

        await subscriberTask.WaitAsync(TimeSpan.FromSeconds(5));

        await _inner.Received(1).DrainAsync(Arg.Any<CancellationToken>());

        await Assert.That(subscriberCompleted).IsTrue();
    }
}
