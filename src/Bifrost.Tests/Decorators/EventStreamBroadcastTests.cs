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
public class EventStreamBroadcastTests
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
    /// Verifies that multiple subscribers receive the same event (broadcast pattern).
    /// </summary>
    [Test]
    public async Task GetEventStreamAsync_MultipleSubscribers_AllReceiveSameEvent()
    {
        // Arrange
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var subscriber1Events = new ConcurrentBag<IOrchestratorEvent>();
        var subscriber2Events = new ConcurrentBag<IOrchestratorEvent>();
        var subscriber3Events = new ConcurrentBag<IOrchestratorEvent>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Start three subscribers
        var subscriber1Task = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token).ConfigureAwait(false))
            {
                subscriber1Events.Add(evt);
                if (subscriber1Events.Count >= 2)
                {
                    break;
                }
            }
        });

        var subscriber2Task = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token).ConfigureAwait(false))
            {
                subscriber2Events.Add(evt);
                if (subscriber2Events.Count >= 2)
                {
                    break;
                }
            }
        });

        var subscriber3Task = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token).ConfigureAwait(false))
            {
                subscriber3Events.Add(evt);
                if (subscriber3Events.Count >= 2)
                {
                    break;
                }
            }
        });

        // Give subscribers time to start
        await Task.Delay(100).ConfigureAwait(false);

        // Act - enqueue work to generate events
        await decorator.EnqueueAsync("test1").ConfigureAwait(false);
        await decorator.EnqueueAsync("test2").ConfigureAwait(false);

        // Wait for all subscribers to receive events
        await Task.WhenAll(subscriber1Task, subscriber2Task, subscriber3Task).ConfigureAwait(false);

        // Assert - all subscribers should receive both events
        await Assert.That(subscriber1Events.Count).IsEqualTo(2);
        await Assert.That(subscriber2Events.Count).IsEqualTo(2);
        await Assert.That(subscriber3Events.Count).IsEqualTo(2);
    }

    /// <summary>
    /// Verifies that slow subscribers use DropOldest and don't block other subscribers.
    /// </summary>
    [Test]
    public async Task GetEventStreamAsync_SlowSubscriber_DoesNotBlockOthers()
    {
        // Arrange
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var fastSubscriberEvents = new ConcurrentBag<IOrchestratorEvent>();
        var slowSubscriberReady = new TaskCompletionSource();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Start a slow subscriber that doesn't consume events quickly
        var slowSubscriberTask = Task.Run(async () =>
        {
            try
            {
                var isFirst = true;
                await foreach (var evt in decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token).ConfigureAwait(false))
                {
                    if (isFirst)
                    {
                        slowSubscriberReady.TrySetResult();
                        isFirst = false;
                    }

                    // Simulate slow processing - just don't consume quickly
                    await Task.Delay(500, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation
            }
        });

        // Start a fast subscriber
        var fastSubscriberTask = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token).ConfigureAwait(false))
            {
                fastSubscriberEvents.Add(evt);
                if (fastSubscriberEvents.Count >= 10)
                {
                    break;
                }
            }
        });

        // Give subscribers time to start
        await Task.Delay(100).ConfigureAwait(false);

        // Enqueue first event to wake up slow subscriber
        await decorator.EnqueueAsync("trigger").ConfigureAwait(false);

        // Wait for slow subscriber to receive first event
        await slowSubscriberReady.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        // Act - enqueue many events rapidly while slow subscriber is still processing
        for (int i = 0; i < 9; i++)
        {
            await decorator.EnqueueAsync($"test{i}").ConfigureAwait(false);
        }

        // Assert - fast subscriber should receive all events without being blocked
        var completed = await Task.WhenAny(fastSubscriberTask, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        await Assert.That(ReferenceEquals(completed, fastSubscriberTask)).IsTrue();
        await Assert.That(fastSubscriberEvents.Count).IsEqualTo(10);

        await cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that subscriber cleanup happens on cancellation.
    /// </summary>
    [Test]
    public async Task GetEventStreamAsync_OnCancellation_CleansUpSubscriber()
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

        // Give subscriber time to start (CI can be slow)
        await Task.Delay(500).ConfigureAwait(false);

        // Generate some events
        await decorator.EnqueueAsync("test1").ConfigureAwait(false);
        await Task.Delay(200).ConfigureAwait(false);

        // Act - cancel the subscriber
        await subscriberCts.CancelAsync().ConfigureAwait(false);
        await subscriberTask.ConfigureAwait(false);

        // Wait a bit then try generating more events
        await Task.Delay(100).ConfigureAwait(false);
        await decorator.EnqueueAsync("test2").ConfigureAwait(false);

        // Assert - no errors should occur, and previous subscriber should have been cleaned up
        // We verify this indirectly by ensuring no exceptions occurred and the orchestrator still works
        await Assert.That(eventsReceived).IsGreaterThanOrEqualTo(1);
    }

    /// <summary>
    /// Verifies that each subscriber gets its own independent channel.
    /// </summary>
    [Test]
    public async Task GetEventStreamAsync_EachSubscriber_GetsOwnChannel()
    {
        // Arrange
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var subscriber1Received = new TaskCompletionSource<IOrchestratorEvent>();
        var subscriber2Received = new TaskCompletionSource<IOrchestratorEvent>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Start two subscribers
        var task1 = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token).ConfigureAwait(false))
            {
                subscriber1Received.TrySetResult(evt);
                break;
            }
        });

        var task2 = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token).ConfigureAwait(false))
            {
                subscriber2Received.TrySetResult(evt);
                break;
            }
        });

        // Give subscribers time to start
        await Task.Delay(100).ConfigureAwait(false);

        // Act
        await decorator.EnqueueAsync("broadcast-test").ConfigureAwait(false);

        // Assert - both subscribers should receive the same event
        var event1 = await subscriber1Received.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        var event2 = await subscriber2Received.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

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
    [Test]
    public async Task TryEnqueue_BroadcastsToMultipleSubscribers()
    {
        // Arrange
        _inner.TryEnqueue(Arg.Any<string>()).Returns(true);
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var subscriber1Events = new ConcurrentBag<IOrchestratorEvent>();
        var subscriber2Events = new ConcurrentBag<IOrchestratorEvent>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var task1 = Task.Run(async () =>
        {
            await foreach (var evt in decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token).ConfigureAwait(false))
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
            await foreach (var evt in decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token).ConfigureAwait(false))
            {
                subscriber2Events.Add(evt);
                if (subscriber2Events.Count >= 1)
                {
                    break;
                }
            }
        });

        // Give subscribers time to start
        await Task.Delay(100).ConfigureAwait(false);

        // Act
        decorator.TryEnqueue("test-work");

        // Wait for subscribers
        await Task.WhenAll(task1, task2).ConfigureAwait(false);

        // Assert
        await Assert.That(subscriber1Events.Count).IsEqualTo(1);
        await Assert.That(subscriber2Events.Count).IsEqualTo(1);
    }

    /// <summary>
    /// Verifies that dispose completes all subscriber channels.
    /// </summary>
    [Test]
    public async Task DisposeAsync_CompletesAllSubscriberChannels()
    {
        // Arrange
        var decorator = new EventStreamOrchestrator<string>(_inner, _logger);
        var subscriber1Completed = new TaskCompletionSource();
        var subscriber2Completed = new TaskCompletionSource();

        var task1 = Task.Run(async () =>
        {
            await foreach (var _ in decorator.GetEventStreamAsync<IOrchestratorEvent>().ConfigureAwait(false))
            {
                // Just consume
            }

            subscriber1Completed.SetResult();
        });

        var task2 = Task.Run(async () =>
        {
            await foreach (var _ in decorator.GetEventStreamAsync<IOrchestratorEvent>().ConfigureAwait(false))
            {
                // Just consume
            }

            subscriber2Completed.SetResult();
        });

        // Give subscribers time to start
        await Task.Delay(100).ConfigureAwait(false);

        // Act
        await decorator.DisposeAsync().ConfigureAwait(false);

        // Assert - both subscribers should complete gracefully
        var completedTask1 = await Task.WhenAny(subscriber1Completed.Task, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        var completedTask2 = await Task.WhenAny(subscriber2Completed.Task, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);

        await Assert.That(ReferenceEquals(completedTask1, subscriber1Completed.Task)).IsTrue();
        await Assert.That(ReferenceEquals(completedTask2, subscriber2Completed.Task)).IsTrue();
    }
}
