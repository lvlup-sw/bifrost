// =============================================================================
// <copyright file="EventStreamAndDlqCorrectnessTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.Core.Events;
using Bifrost.DeadLetter;
using Bifrost.Decorators;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bifrost.Tests.Decorators;

/// <summary>
/// Correctness-by-construction regression tests for <see cref="EventStreamOrchestrator{TWork}"/>
/// and <see cref="RejectionRoutingOrchestrator{TWork}"/>.
/// </summary>
[ParallelLimiter<EventStreamParallelLimit>]
public sealed class EventStreamAndDlqCorrectnessTests
{
    /// <summary>
    /// Verifies that subscribing before <see cref="EventStreamOrchestrator{TWork}.DisposeAsync"/>
    /// completes the stream cleanly (even if enumeration starts after disposal), and that
    /// subscribing after disposal throws <see cref="ObjectDisposedException"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task EventStream_SubscribeAcrossDispose_NeverStrandsChannel()
    {
        var decorator = new EventStreamOrchestrator<string>(
            new StubOrchestrator(),
            NullLogger<EventStreamOrchestrator<string>>.Instance);

        var stream = decorator.GetEventStreamAsync<IOrchestratorEvent>();
        await decorator.DisposeAsync().ConfigureAwait(false);

        // Even though enumeration starts after DisposeAsync, the registered channel was
        // completed by DisposeAsync and terminates immediately without hanging.
        var count = 0;
        await foreach (var _ in stream.ConfigureAwait(false))
        {
            count++;
        }

        await Assert.That(count).IsEqualTo(0);

        await Assert.That(() =>
        {
            _ = decorator.GetEventStreamAsync<IOrchestratorEvent>();
        }).Throws<ObjectDisposedException>();
    }

    /// <summary>
    /// Verifies that calling <see cref="EventStreamOrchestrator{TWork}.GetEventStreamAsync{TEvent}"/>
    /// while <see cref="EventStreamOrchestrator{TWork}.DrainAsync"/> is running (or after it
    /// has completed) returns an already-completed stream instead of hanging.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task EventStream_SubscribeDuringAndAfterDrain_CompletesImmediately()
    {
        var drainEntered = new SemaphoreSlim(0);
        var releaseDrain = new SemaphoreSlim(0);
        var inner = new StubOrchestrator(async ct =>
        {
            drainEntered.Release();
            await releaseDrain.WaitAsync(ct).ConfigureAwait(false);
        });

        var decorator = new EventStreamOrchestrator<string>(
            inner,
            NullLogger<EventStreamOrchestrator<string>>.Instance);

        var drainTask = decorator.DrainAsync();
        await drainEntered.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        var countDuringDrain = 0;
        await foreach (var _ in decorator.GetEventStreamAsync<IOrchestratorEvent>().ConfigureAwait(false))
        {
            countDuringDrain++;
        }

        await Assert.That(countDuringDrain).IsEqualTo(0);

        releaseDrain.Release();
        await drainTask.ConfigureAwait(false);

        var countAfterDrain = 0;
        await foreach (var _ in decorator.GetEventStreamAsync<IOrchestratorEvent>().ConfigureAwait(false))
        {
            countAfterDrain++;
        }

        await Assert.That(countAfterDrain).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies under concurrent producers and drains in <see cref="DeadLetterQueue{TWork}"/>
    /// that <see cref="DeadLetterQueue{TWork}.Count"/> never exceeds capacity and
    /// <c>drained + remaining + dropped == total</c>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DeadLetterQueue_ConcurrentEnqueueAndDrain_PreservesExactCountAndDropAccounting()
    {
        const int capacity = 8;
        const int producers = 4;
        const int itemsPerProducer = 250;
        var dlq = new DeadLetterQueue<int>(
            Options.Create(new DeadLetterQueueOptions { Capacity = capacity }),
            NullLogger<DeadLetterQueue<int>>.Instance);

        var drained = 0;
        var startGate = new SemaphoreSlim(0);

        var producerTasks = Enumerable.Range(0, producers).Select(p => Task.Run(async () =>
        {
            await startGate.WaitAsync().ConfigureAwait(false);
            for (int i = 0; i < itemsPerProducer; i++)
            {
                await dlq.EnqueueAsync(new DeadLetteredWork<int>(
                    (p * itemsPerProducer) + i, null, 1, DateTimeOffset.UtcNow, null)).ConfigureAwait(false);
            }
        })).ToArray();

        var consumerTask = Task.Run(async () =>
        {
            await startGate.WaitAsync().ConfigureAwait(false);
            while (producerTasks.Any(t => !t.IsCompleted))
            {
                await foreach (var _ in dlq.ReadAllAsync().ConfigureAwait(false))
                {
                    Interlocked.Increment(ref drained);
                }

                await Task.Yield();
            }
        });

        startGate.Release(producers + 1);
        await Task.WhenAll(producerTasks).ConfigureAwait(false);
        await consumerTask.ConfigureAwait(false);

        await foreach (var _ in dlq.ReadAllAsync().ConfigureAwait(false))
        {
            Interlocked.Increment(ref drained);
        }

        await Assert.That(dlq.Count).IsEqualTo(0);
        await Assert.That(drained + dlq.DroppedCount).IsEqualTo((long)(producers * itemsPerProducer));
    }

    /// <summary>
    /// Verifies that <see cref="RejectionRoutingOrchestrator{TWork}.DrainAsync"/> waits
    /// for in-flight asynchronous dead-letter routing initiated by synchronous
    /// <see cref="RejectionRoutingOrchestrator{TWork}.TryEnqueue"/> before completing.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task RejectionRouting_DrainAsync_WaitsForInFlightSyncEnqueueRoutes()
    {
        var inner = new StubOrchestrator
        {
            EnqueueResultToReturn = EnqueueResult.Rejected(RejectionReason.CapacityExceeded),
        };
        var dlq = new AsyncGatedDeadLetterQueue();
        var routing = new RejectionRoutingOrchestrator<string>(
            inner, dlq, notifier: null, NullLogger<RejectionRoutingOrchestrator<string>>.Instance);

        var accepted = routing.TryEnqueue("shed-item");
        await Assert.That(accepted).IsFalse();

        await dlq.WaitForEnqueueEnteredAsync().ConfigureAwait(false);

        var drainTask = routing.DrainAsync();
        await Task.Yield();
        await Assert.That(drainTask.IsCompleted).IsFalse();

        dlq.ReleaseEnqueue();
        await drainTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Assert.That(dlq.Count).IsEqualTo(1);
    }

    private sealed class StubOrchestrator(Func<CancellationToken, Task>? onDrain = null) : IWorkOrchestrator<string>
    {
        public EnqueueResult EnqueueResultToReturn { get; init; } = EnqueueResult.Accepted;

        public int PendingCount => 0;

        public int ActiveWorkers => 0;

        public int Capacity => 10;

        public Task Completion => Task.CompletedTask;

        public EnqueueResult Enqueue(string work, WorkEnqueueOptions options = default) => this.EnqueueResultToReturn;

        public bool TryEnqueue(string work, WorkEnqueueOptions options = default) => this.Enqueue(work, options).IsAccepted;

        public ValueTask<EnqueueResult> EnqueueAsync(string work, WorkEnqueueOptions options = default, CancellationToken ct = default)
            => ValueTask.FromResult(this.EnqueueResultToReturn);

        public void Complete()
        {
        }

        public Task DrainAsync(CancellationToken ct = default)
            => onDrain is not null ? onDrain(ct) : Task.CompletedTask;

        public Task AbortAsync(CancellationToken ct = default) => Task.CompletedTask;

        public CancellationToken GetShutdownToken() => CancellationToken.None;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Func<string, CancellationToken, Task> CreateWorkerFunction()
            => (_, _) => Task.CompletedTask;

        public Func<string, CancellationToken, Task> CreateWorkerFunction(Action<bool>? stateCallback)
            => (_, _) => Task.CompletedTask;

        public Task RequestScaleUpAsync(int count, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RequestScaleDownAsync(int count, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class AsyncGatedDeadLetterQueue : IDeadLetterQueue<string>
    {
        private readonly ConcurrentQueue<DeadLetteredWork<string>> items = new();
        private readonly SemaphoreSlim entered = new(0);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Count => this.items.Count;

        public async ValueTask EnqueueAsync(DeadLetteredWork<string> item, CancellationToken ct = default)
        {
            this.entered.Release();
            await this.release.Task.WaitAsync(ct).ConfigureAwait(false);
            this.items.Enqueue(item);
        }

        public async IAsyncEnumerable<DeadLetteredWork<string>> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            while (!ct.IsCancellationRequested && this.items.TryDequeue(out var item))
            {
                yield return item;
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        public async Task WaitForEnqueueEnteredAsync()
            => await this.entered.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        public void ReleaseEnqueue() => this.release.TrySetResult();
    }
}
