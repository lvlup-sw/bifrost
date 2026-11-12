// =============================================================================
// <copyright file="ObserverBoundaryTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Core.Events;
using Bifrost.Core.DeadLetter;
using Bifrost.DeadLetter;
using Bifrost.Decorators;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Bifrost.Tests.Decorators;

/// <summary>Regression tests for observer isolation and shutdown.</summary>
public class ObserverBoundaryTests
{
    /// <summary>Blocking callback prefixes cannot hold the publisher or another subscriber.</summary>
    [Test]
    // This test deliberately parks a pool thread. Do not compete with the full
    // orchestrator suite while measuring whether another callback can start.
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BlockingSubscriberDoesNotBlockPublisherOrOtherSubscribers(bool asyncCallback)
    {
        var notifier = new DeadLetterNotifier<int>(NullLogger<DeadLetterNotifier<int>>.Instance);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var other = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Callback(WorkDeadLetteredEvent<int> evt)
        {
            entered.TrySetResult();
            try
            {
                release.Wait();
                return Task.CompletedTask;
            }
            finally
            {
                exited.TrySetResult();
            }
        }

        using var subscription = asyncCallback
            ? notifier.SubscribeAsync(Callback)
            : notifier.Subscribe(evt => Callback(evt).GetAwaiter().GetResult());
        using var second = notifier.Subscribe(_ => other.TrySetResult());
        var publish = Task.Run(() => notifier.Notify(new(1, null, 1, DateTimeOffset.UtcNow)));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await Task.WhenAll(publish, other.Task).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(publish, exited.Task).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>Every successfully registered subscriber is completed by disposal.</summary>
    [Test]
    public async Task SubscriptionRacingDisposalAlwaysTerminates()
    {
        // Exercise the check/register boundary from both sides. No events are published:
        // every successfully registered empty reader must finish solely through disposal.
        for (var round = 0; round < 2000; round++)
        {
            var inner = Substitute.For<IWorkOrchestrator<int>>();
            var decorator = new EventStreamOrchestrator<int>(inner, NullLogger<EventStreamOrchestrator<int>>.Instance);
            using var cancellation = new CancellationTokenSource();
            using var start = new ManualResetEventSlim();
            var subscribe = Task.Run(() =>
            {
                start.Wait();
                try
                {
                    return decorator.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cancellation.Token);
                }
                catch (ObjectDisposedException)
                {
                    return null;
                }
            });
            var dispose = Task.Run(async () =>
            {
                start.Wait();
                await decorator.DisposeAsync().ConfigureAwait(false);
            });
            start.Set();
            var stream = await subscribe.ConfigureAwait(false);
            await dispose.ConfigureAwait(false);
            if (stream is null) continue;
            await using var reader = stream.GetAsyncEnumerator();
            var next = reader.MoveNextAsync().AsTask();
            try
            {
                await Assert.That(await next.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false)).IsFalse();
            }
            finally
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                try
                {
                    await next.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    /// <summary>Observability failure cannot consume admitted work without invoking the handler.</summary>
    [Test]
    [Arguments(DispatchStrategy.Fifo, false)]
    [Arguments(DispatchStrategy.PriorityExact, false)]
    [Arguments(DispatchStrategy.PriorityRelaxed, false)]
    [Arguments(DispatchStrategy.Fifo, true)]
    [Arguments(DispatchStrategy.PriorityExact, true)]
    [Arguments(DispatchStrategy.PriorityRelaxed, true)]
    public async Task ThrowingWorkerObserverDoesNotLoseWork(DispatchStrategy strategy, bool stateObserver)
    {
        var handler = Substitute.For<IWorkHandler<int>>();
        await using var orchestrator = new WorkOrchestrator<int>(handler,
            Options.Create(new WorkOrchestratorOptions
            {
                WorkerCount = 0, Capacity = 4, DispatchStrategy = strategy, FullMode = QueueFullMode.Reject,
            }), NullLogger<WorkOrchestrator<int>>.Instance);
        if (!stateObserver) orchestrator.QueueWaitObserved = (_, _) => throw new InvalidOperationException("observer");
        orchestrator.TryEnqueue(1);
        orchestrator.TryEnqueue(2);
        var worker = orchestrator.CreateWorkerFunction(stateObserver ? _ => throw new InvalidOperationException("observer") : null)("observer-test", CancellationToken.None);
        await orchestrator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await worker.ConfigureAwait(false);
        await handler.Received(1).HandleAsync(1, Arg.Any<CancellationToken>()).ConfigureAwait(false);
        await handler.Received(1).HandleAsync(2, Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>Telemetry failure cannot suppress admission results or dead-letter routing.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ThrowingRejectionObserverDoesNotSuppressRouting(bool asynchronous)
    {
        var inner = Substitute.For<IWorkOrchestrator<int>>();
        inner.Enqueue(Arg.Any<int>(), Arg.Any<WorkEnqueueOptions>()).Returns(EnqueueResult.Rejected(RejectionReason.CapacityExceeded));
        inner.EnqueueAsync(Arg.Any<int>(), Arg.Any<WorkEnqueueOptions>(), Arg.Any<CancellationToken>()).Returns(EnqueueResult.Rejected(RejectionReason.CapacityExceeded));
        var dlq = new DeadLetterQueue<int>(Options.Create(new DeadLetterQueueOptions()), NullLogger<DeadLetterQueue<int>>.Instance);
        await using var routing = new RejectionRoutingOrchestrator<int>(inner, dlq, null, NullLogger<RejectionRoutingOrchestrator<int>>.Instance);
        routing.RejectionObserved = (_, _) => throw new InvalidOperationException("observer");
        var result = asynchronous ? await routing.EnqueueAsync(1).ConfigureAwait(false) : routing.Enqueue(1);
        await Assert.That(result.Reason).IsEqualTo(RejectionReason.CapacityExceeded);
        await Assert.That(dlq.Count).IsEqualTo(1);
    }
}
