// =============================================================================
// <copyright file="EventStreamBroadcastTests.cs" company="Levelup Software">
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
/// Tests for event broadcast pattern in <see cref="EventStreamOrchestrator{TWork}"/>.
/// </summary>
/// <remarks>
/// Each test calls <c>GetEventStreamAsync</c> on the test thread before it publishes, and only
/// enumerates the stream inside <c>Task.Run</c>. The call registers the subscriber channel before
/// it returns, and the channel buffers events until the reader starts. So no event can be missed,
/// and no test waits a fixed time for a subscriber to start. Under CPU load, a subscription made
/// inside <c>Task.Run</c> after a fixed delay could register after the publish and miss the event.
/// </remarks>
[ParallelLimiter<EventStreamParallelLimit>]
public class EventStreamBroadcastTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

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
    /// Verifies that multiple subscribers receive the same event (broadcast pattern).
    /// </summary>
    /// <remarks>
    /// The test registers three subscribers. Then it enqueues work to make events and waits for all
    /// subscribers to receive them. All
    /// subscribers must receive both events.
    /// </remarks>
    [Test]
    public async Task GetEventStreamAsync_MultipleSubscribers_AllReceiveSameEvent()
    {
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var subscriber1Events = new ConcurrentBag<IOrchestratorEvent>();
        var subscriber2Events = new ConcurrentBag<IOrchestratorEvent>();
        var subscriber3Events = new ConcurrentBag<IOrchestratorEvent>();

        using var cts = new CancellationTokenSource(WaitTimeout);

        var stream1 = decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token);
        var subscriber1Task = Task.Run(async () =>
        {
            await foreach (var evt in stream1)
            {
                subscriber1Events.Add(evt);
                if (subscriber1Events.Count >= 2)
                {
                    break;
                }
            }
        });

        var stream2 = decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token);
        var subscriber2Task = Task.Run(async () =>
        {
            await foreach (var evt in stream2)
            {
                subscriber2Events.Add(evt);
                if (subscriber2Events.Count >= 2)
                {
                    break;
                }
            }
        });

        var stream3 = decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token);
        var subscriber3Task = Task.Run(async () =>
        {
            await foreach (var evt in stream3)
            {
                subscriber3Events.Add(evt);
                if (subscriber3Events.Count >= 2)
                {
                    break;
                }
            }
        });

        await decorator.EnqueueAsync("test1");
        await decorator.EnqueueAsync("test2");

        await Task.WhenAll(subscriber1Task, subscriber2Task, subscriber3Task);

        await Assert.That(subscriber1Events.Count).IsEqualTo(2);
        await Assert.That(subscriber2Events.Count).IsEqualTo(2);
        await Assert.That(subscriber3Events.Count).IsEqualTo(2);
    }

    /// <summary>
    /// Verifies that slow subscribers use DropOldest and don't block other subscribers.
    /// </summary>
    /// <remarks>
    /// The slow subscriber does not consume events quickly: it waits 500 ms for each event to
    /// simulate slow processing. The <c>OperationCanceledException</c> at cancellation is expected.
    /// The first enqueue wakes the slow subscriber, and the test waits until the slow subscriber
    /// receives that first event. Then the test enqueues many events rapidly while the slow
    /// subscriber is still busy. The fast subscriber must receive all events without being blocked.
    /// </remarks>
    [Test]
    public async Task GetEventStreamAsync_SlowSubscriber_DoesNotBlockOthers()
    {
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var fastSubscriberEvents = new ConcurrentBag<IOrchestratorEvent>();
        var slowSubscriberReady = new TaskCompletionSource();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var stream1 = decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token);
        var slowSubscriberTask = Task.Run(async () =>
        {
            try
            {
                var isFirst = true;
                await foreach (var evt in stream1)
                {
                    if (isFirst)
                    {
                        slowSubscriberReady.TrySetResult();
                        isFirst = false;
                    }

                    await Task.Delay(500, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        var stream2 = decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token);
        var fastSubscriberTask = Task.Run(async () =>
        {
            await foreach (var evt in stream2)
            {
                fastSubscriberEvents.Add(evt);
                if (fastSubscriberEvents.Count >= 10)
                {
                    break;
                }
            }
        });

        await decorator.EnqueueAsync("trigger");

        await slowSubscriberReady.Task.WaitAsync(WaitTimeout);

        for (int i = 0; i < 9; i++)
        {
            await decorator.EnqueueAsync($"test{i}");
        }

        var completed = await Task.WhenAny(fastSubscriberTask, Task.Delay(WaitTimeout));
        await Assert.That(ReferenceEquals(completed, fastSubscriberTask)).IsTrue();
        await Assert.That(fastSubscriberEvents.Count).IsEqualTo(10);

        await cts.CancelAsync();
    }

    /// <summary>
    /// Verifies that subscriber cleanup happens on cancellation.
    /// </summary>
    /// <remarks>
    /// The <c>OperationCanceledException</c> in the subscriber is expected. The test makes an event,
    /// waits until the subscriber reads it, cancels the subscriber, and then makes more events. No errors must occur, and the
    /// cancelled subscriber must be cleaned up. The test checks this indirectly: no exceptions
    /// occur and the orchestrator still works.
    /// </remarks>
    [Test]
    public async Task GetEventStreamAsync_OnCancellation_CleansUpSubscriber()
    {
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var subscriberCts = new CancellationTokenSource();
        var eventsReceived = 0;
        var firstEventRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var stream = decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: subscriberCts.Token);
        var subscriberTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in stream)
                {
                    eventsReceived++;
                    firstEventRead.TrySetResult();
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        await decorator.EnqueueAsync("test1");
        await firstEventRead.Task.WaitAsync(WaitTimeout);

        await subscriberCts.CancelAsync();
        await subscriberTask;

        await Task.Delay(100);
        await decorator.EnqueueAsync("test2");

        await Assert.That(eventsReceived).IsGreaterThanOrEqualTo(1);
    }

    /// <summary>
    /// Verifies that each subscriber gets its own independent channel.
    /// </summary>
    /// <remarks>
    /// Both subscribers must receive the same event.
    /// </remarks>
    [Test]
    public async Task GetEventStreamAsync_EachSubscriber_GetsOwnChannel()
    {
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var subscriber1Received = new TaskCompletionSource<IOrchestratorEvent>();
        var subscriber2Received = new TaskCompletionSource<IOrchestratorEvent>();

        using var cts = new CancellationTokenSource(WaitTimeout);

        var stream1 = decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token);
        var task1 = Task.Run(async () =>
        {
            await foreach (var evt in stream1)
            {
                subscriber1Received.TrySetResult(evt);
                break;
            }
        });

        var stream2 = decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token);
        var task2 = Task.Run(async () =>
        {
            await foreach (var evt in stream2)
            {
                subscriber2Received.TrySetResult(evt);
                break;
            }
        });

        await decorator.EnqueueAsync("broadcast-test");

        var event1 = await subscriber1Received.Task.WaitAsync(WaitTimeout);
        var event2 = await subscriber2Received.Task.WaitAsync(WaitTimeout);

        await Assert.That(event1 is WorkEnqueuedEvent<string>).IsTrue();
        await Assert.That(event2 is WorkEnqueuedEvent<string>).IsTrue();

        var enqueued1 = (WorkEnqueuedEvent<string>)event1;
        var enqueued2 = (WorkEnqueuedEvent<string>)event2;

        await Assert.That(enqueued1.Work).IsEqualTo("broadcast-test");
        await Assert.That(enqueued2.Work).IsEqualTo("broadcast-test");
    }

    /// <summary>
    /// Verifies that TryEnqueue broadcasts events to multiple subscribers.
    /// </summary>
    /// <remarks>
    /// The test registers the subscribers synchronously. <c>GetEventStreamAsync</c> adds to the
    /// subscriber dictionary before it returns the async enumerable, so both channels exist before
    /// any enqueue. The bounded channel (capacity 1000) buffers events, even if the readers have
    /// not started to iterate. Thus no delay is necessary before the enqueue.
    /// </remarks>
    [Test]
    public async Task TryEnqueue_BroadcastsToMultipleSubscribers()
    {
        _inner.Enqueue(Arg.Any<string>()).Returns(EnqueueResult.Accepted);
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var subscriber1Events = new ConcurrentBag<IOrchestratorEvent>();
        var subscriber2Events = new ConcurrentBag<IOrchestratorEvent>();

        using var cts = new CancellationTokenSource(WaitTimeout);

        var stream1 = decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token);
        var stream2 = decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token);

        var task1 = Task.Run(async () =>
        {
            await foreach (var evt in stream1)
            {
                subscriber1Events.Add(evt);
                if (subscriber1Events.Count >= 1)
                {
                    break;
                }
            }
        });

        var task2 = Task.Run(async () =>
        {
            await foreach (var evt in stream2)
            {
                subscriber2Events.Add(evt);
                if (subscriber2Events.Count >= 1)
                {
                    break;
                }
            }
        });

        decorator.TryEnqueue("test-work");

        await Task.WhenAll(task1, task2);

        await Assert.That(subscriber1Events.Count).IsEqualTo(1);
        await Assert.That(subscriber2Events.Count).IsEqualTo(1);
    }

    /// <summary>
    /// Verifies that dispose completes all subscriber channels.
    /// </summary>
    /// <remarks>
    /// Both subscribers only consume events. They must complete normally when the decorator is
    /// disposed.
    /// </remarks>
    [Test]
    public async Task DisposeAsync_CompletesAllSubscriberChannels()
    {
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var subscriber1Completed = new TaskCompletionSource();
        var subscriber2Completed = new TaskCompletionSource();

        var stream1 = decorator.GetEventStreamAsync<IOrchestratorEvent>();
        var task1 = Task.Run(async () =>
        {
            await foreach (var _ in stream1)
            {
            }

            subscriber1Completed.SetResult();
        });

        var stream2 = decorator.GetEventStreamAsync<IOrchestratorEvent>();
        var task2 = Task.Run(async () =>
        {
            await foreach (var _ in stream2)
            {
            }

            subscriber2Completed.SetResult();
        });

        await decorator.DisposeAsync();

        var completedTask1 = await Task.WhenAny(subscriber1Completed.Task, Task.Delay(WaitTimeout));
        var completedTask2 = await Task.WhenAny(subscriber2Completed.Task, Task.Delay(WaitTimeout));

        await Assert.That(ReferenceEquals(completedTask1, subscriber1Completed.Task)).IsTrue();
        await Assert.That(ReferenceEquals(completedTask2, subscriber2Completed.Task)).IsTrue();
    }
}
