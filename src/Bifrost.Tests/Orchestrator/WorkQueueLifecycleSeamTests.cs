// =============================================================================
// <copyright file="WorkQueueLifecycleSeamTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;

using Bifrost.Core;
using Bifrost.Queues;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Orchestrator;

/// <summary>
/// Tests for the internal dispatch-queue seam: <see cref="WorkOrchestrator{TWork}"/> drives queue
/// completion, the retirement wake hand-off, and the matched-wake take through
/// <see cref="IWorkQueueLifecycle"/> instead of pattern-matching on concrete bindings, and an
/// internal constructor accepts an injected queue factory.
/// </summary>
/// <remarks>
/// The injected queue is a recording fake that implements <see cref="IWorkQueue{T}"/> and
/// <see cref="IWorkQueueLifecycle"/> and counts each lifecycle call. Workers are absent or
/// single so that the counted calls are deterministic.
/// </remarks>
public sealed class WorkQueueLifecycleSeamTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Every built-in binding implements the lifecycle seam, and a drain completes.</summary>
    /// <param name="strategy">The dispatch strategy under test.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    [Arguments(DispatchStrategy.Fifo)]
    [Arguments(DispatchStrategy.PriorityExact)]
    [Arguments(DispatchStrategy.PriorityRelaxed)]
    public async Task BuiltInBinding_ImplementsLifecycle_AndDrainCompletes(DispatchStrategy strategy)
    {
        var handler = new CountingHandler();
        await using var orchestrator = new WorkOrchestrator<string>(
            handler,
            Options.Create(new WorkOrchestratorOptions
            {
                WorkerCount = 1,
                DispatchStrategy = strategy,
                FullMode = QueueFullMode.Reject,
            }),
            NullLogger<WorkOrchestrator<string>>.Instance);

        await Assert.That(orchestrator.WorkQueue is IWorkQueueLifecycle).IsTrue();

        await Assert.That(orchestrator.Enqueue("a").IsAccepted).IsTrue();
        await Assert.That(orchestrator.Enqueue("b").IsAccepted).IsTrue();
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);

        await Assert.That(handler.Count).IsEqualTo(2);
        await Assert.That(orchestrator.PendingCount).IsEqualTo(0);
    }

    /// <summary>The internal factory constructor routes admission to the injected queue.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task FactoryConstructor_UsesInjectedQueue()
    {
        var queue = new RecordingWorkQueue();
        var timeProvider = new FakeTimeProvider();
        WorkOrchestratorOptions? seenOptions = null;
        TimeProvider? seenTime = null;
        var options = new WorkOrchestratorOptions { WorkerCount = 0, FullMode = QueueFullMode.Reject };
        await using var orchestrator = new WorkOrchestrator<string>(
            new CountingHandler(),
            Options.Create(options),
            NullLogger<WorkOrchestrator<string>>.Instance,
            timeProvider,
            null,
            (opts, time) =>
            {
                seenOptions = opts;
                seenTime = time;
                return queue;
            });

        await Assert.That(orchestrator.WorkQueue).IsSameReferenceAs(queue);
        await Assert.That(seenOptions).IsSameReferenceAs(options);
        await Assert.That(seenTime).IsSameReferenceAs(timeProvider);

        await Assert.That(orchestrator.Enqueue("x").IsAccepted).IsTrue();
        var result = await orchestrator.EnqueueAsync("y");
        await Assert.That(result.IsAccepted).IsTrue();

        await Assert.That(queue.Admitted.Select(static e => e.Work).ToArray()).IsEquivalentTo(new[] { "x", "y" });
        await Assert.That(orchestrator.PendingCount).IsEqualTo(2);
    }

    /// <summary>A second drain does not complete the queue again.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DrainAsync_CalledTwice_CompletesQueueOnce()
    {
        var queue = new RecordingWorkQueue();
        var handler = new CountingHandler();
        await using var orchestrator = CreateWithQueue(handler, queue, workerCount: 1);

        await Assert.That(orchestrator.Enqueue("a").IsAccepted).IsTrue();
        await Assert.That(orchestrator.Enqueue("b").IsAccepted).IsTrue();

        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);

        await Assert.That(queue.CompleteCalls).IsEqualTo(1);
        await Assert.That(handler.Count).IsEqualTo(2);
        await Assert.That(queue.WakeCalls).IsEqualTo(0);
    }

    /// <summary>
    /// Each extra item the greedy inner loop takes consumes its matched wake through the seam.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// The items are queued before the only worker starts, so the first wait funds the first item
    /// and the inner loop takes the other two.
    /// </remarks>
    [Test]
    public async Task GreedyInnerLoop_ConsumesMatchedWakeThroughSeam()
    {
        var queue = new RecordingWorkQueue();
        var handler = new CountingHandler();
        await using var orchestrator = CreateWithQueue(handler, queue, workerCount: 0);

        await Assert.That(orchestrator.Enqueue("a").IsAccepted).IsTrue();
        await Assert.That(orchestrator.Enqueue("b").IsAccepted).IsTrue();
        await Assert.That(orchestrator.Enqueue("c").IsAccepted).IsTrue();

        var worker = orchestrator.CreateWorkerFunction();
        var run = worker("Dynamic", CancellationToken.None);
        await handler.WaitForCountAsync(3).WaitAsync(WaitTimeout);
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        await run.WaitAsync(WaitTimeout);

        await Assert.That(queue.MatchedWakeCalls).IsEqualTo(2);
    }

    /// <summary>A retiring worker hands a wake to the remaining consumers through the seam.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task RetiredWorker_HandsOffWakeThroughSeam()
    {
        var queue = new RecordingWorkQueue();
        await using var orchestrator = CreateWithQueue(new CountingHandler(), queue, workerCount: 0);
        using var retirement = new CancellationTokenSource();

        var worker = orchestrator.CreateWorkerFunction(null, retirement.Token);
        var run = worker("Dynamic", CancellationToken.None);
        await retirement.CancelAsync();
        await run.WaitAsync(WaitTimeout);

        await Assert.That(queue.WakeCalls).IsEqualTo(1);
        await Assert.That(queue.CompleteCalls).IsEqualTo(0);
    }

    /// <summary>
    /// A retiring worker on a built-in priority binding restores one wake permit through the
    /// binding's lifecycle forwarder.
    /// </summary>
    /// <param name="strategy">The priority strategy under test.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// No item is queued, so the worker parks on the wait without taking a permit. Cancelling
    /// retirement ends that wait and the worker's exit path hands off one wake, which shows up as
    /// one extra pending permit on the binding's semaphore.
    /// </remarks>
    [Test]
    [Arguments(DispatchStrategy.PriorityExact)]
    [Arguments(DispatchStrategy.PriorityRelaxed)]
    public async Task RetiredWorker_OnBuiltInPriorityBinding_RestoresOneWakePermit(DispatchStrategy strategy)
    {
        await using var orchestrator = new WorkOrchestrator<string>(
            new CountingHandler(),
            Options.Create(new WorkOrchestratorOptions
            {
                WorkerCount = 0,
                DispatchStrategy = strategy,
                FullMode = QueueFullMode.Reject,
            }),
            NullLogger<WorkOrchestrator<string>>.Instance);
        using var retirement = new CancellationTokenSource();
        var before = PendingWakes(orchestrator.WorkQueue);

        var worker = orchestrator.CreateWorkerFunction(null, retirement.Token);
        var run = worker("Dynamic", CancellationToken.None);
        await retirement.CancelAsync();
        await run.WaitAsync(WaitTimeout);

        await Assert.That(PendingWakes(orchestrator.WorkQueue)).IsEqualTo(before + 1);
    }

    /// <summary>An injected queue without the lifecycle seam is rejected at construction.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task FactoryConstructor_QueueWithoutLifecycle_Throws()
    {
        await Assert.That(() => new WorkOrchestrator<string>(
                new CountingHandler(),
                Options.Create(new WorkOrchestratorOptions { WorkerCount = 0, FullMode = QueueFullMode.Reject }),
                NullLogger<WorkOrchestrator<string>>.Instance,
                null,
                null,
                static (_, _) => new NonLifecycleWorkQueue()))
            .Throws<ArgumentException>();
    }

    /// <summary>Wait full mode with an injected non-FIFO queue is rejected at construction.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task FactoryConstructor_NonFifoQueueWithWaitMode_Throws()
    {
        var queue = new RecordingWorkQueue();
        await Assert.That(() => new WorkOrchestrator<string>(
                new CountingHandler(),
                Options.Create(new WorkOrchestratorOptions { WorkerCount = 0, FullMode = QueueFullMode.Wait }),
                NullLogger<WorkOrchestrator<string>>.Instance,
                null,
                null,
                (_, _) => queue))
            .Throws<ArgumentException>();
    }

    /// <summary>Wait full mode with an injected FIFO channel queue is accepted and still waits.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task FactoryConstructor_FifoQueueWithWaitMode_IsAccepted()
    {
        await using var orchestrator = new WorkOrchestrator<string>(
            new CountingHandler(),
            Options.Create(new WorkOrchestratorOptions { WorkerCount = 0, Capacity = 1, FullMode = QueueFullMode.Wait }),
            NullLogger<WorkOrchestrator<string>>.Instance,
            null,
            null,
            static (opts, _) => new FifoChannelWorkQueue<WorkEnvelope<string>>(opts.Capacity));

        await Assert.That(orchestrator.WorkQueue is FifoChannelWorkQueue<WorkEnvelope<string>>).IsTrue();
        await Assert.That((await orchestrator.EnqueueAsync("a")).IsAccepted).IsTrue();
        var waiting = orchestrator.EnqueueAsync("b").AsTask();
        await Assert.That(waiting.IsCompleted).IsFalse();
        await Assert.That(orchestrator.TryReadEnvelope(out _)).IsTrue();
        await Assert.That((await waiting.WaitAsync(WaitTimeout)).IsAccepted).IsTrue();
    }

    /// <summary>The enum priority path keeps its existing guard message for Wait full mode.</summary>
    /// <param name="strategy">The priority strategy under test.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    [Arguments(DispatchStrategy.PriorityExact)]
    [Arguments(DispatchStrategy.PriorityRelaxed)]
    public async Task PublicConstructor_PriorityWithWaitMode_KeepsMessage(DispatchStrategy strategy)
    {
        var error = await Assert.That(() => new WorkOrchestrator<string>(
                new CountingHandler(),
                Options.Create(new WorkOrchestratorOptions { DispatchStrategy = strategy, FullMode = QueueFullMode.Wait }),
                NullLogger<WorkOrchestrator<string>>.Instance))
            .Throws<ArgumentException>();

        await Assert.That(error!.Message).Contains("Priority dispatch supports only Reject full mode.");
    }

    private static int PendingWakes(IWorkQueue<WorkEnvelope<string>> queue) => queue switch
    {
        LockingPriorityWorkQueue<string> exact => exact.PendingWakeCountForTest,
        ConcurrentPriorityWorkQueue<string> relaxed => relaxed.PendingWakeCountForTest,
        _ => throw new InvalidOperationException("The queue is not a built-in priority binding."),
    };

    private static WorkOrchestrator<string> CreateWithQueue(IWorkHandler<string> handler, RecordingWorkQueue queue, int workerCount)
        => new(
            handler,
            Options.Create(new WorkOrchestratorOptions { WorkerCount = workerCount, FullMode = QueueFullMode.Reject }),
            NullLogger<WorkOrchestrator<string>>.Instance,
            null,
            null,
            (_, _) => queue);

    private sealed class CountingHandler : IWorkHandler<string>
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public ValueTask HandleAsync(string work, CancellationToken ct)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.CompletedTask;
        }

        public async Task WaitForCountAsync(int expected)
        {
            while (Count < expected) await Task.Delay(5);
        }
    }

    private sealed class RecordingWorkQueue : IWorkQueue<WorkEnvelope<string>>, IWorkQueueLifecycle
    {
        private readonly ConcurrentQueue<WorkEnvelope<string>> _items = new();
        private readonly SemaphoreSlim _signal = new(0);
        private volatile bool _completed;
        private int _completeCalls;
        private int _wakeCalls;
        private int _matchedWakeCalls;

        public ConcurrentQueue<WorkEnvelope<string>> Admitted { get; } = new();

        public int CompleteCalls => Volatile.Read(ref _completeCalls);

        public int WakeCalls => Volatile.Read(ref _wakeCalls);

        public int MatchedWakeCalls => Volatile.Read(ref _matchedWakeCalls);

        public int Count => _items.Count;

        public EnqueueResult Enqueue(in WorkEnvelope<string> item)
        {
            if (_completed) return EnqueueResult.Rejected(RejectionReason.Shutdown);
            _items.Enqueue(item);
            Admitted.Enqueue(item);
            _signal.Release();
            return EnqueueResult.Accepted;
        }

        public bool TryEnqueue(in WorkEnvelope<string> item) => Enqueue(item).IsAccepted;

        public async ValueTask<bool> WaitToDequeueAsync(CancellationToken cancellationToken)
        {
            if (_completed && _items.IsEmpty) return false;
            await _signal.WaitAsync(cancellationToken);
            return !(_completed && _items.IsEmpty);
        }

        public bool TryDequeue(out WorkEnvelope<string> item) => _items.TryDequeue(out item);

        public void Complete()
        {
            Interlocked.Increment(ref _completeCalls);
            _completed = true;
            _signal.Release(64);
        }

        public void WakeConsumer() => Interlocked.Increment(ref _wakeCalls);

        public void ConsumeMatchedWake() => Interlocked.Increment(ref _matchedWakeCalls);
    }

    private sealed class NonLifecycleWorkQueue : IWorkQueue<WorkEnvelope<string>>
    {
        public int Count => 0;

        public EnqueueResult Enqueue(in WorkEnvelope<string> item) => EnqueueResult.Accepted;

        public bool TryEnqueue(in WorkEnvelope<string> item) => true;

        public ValueTask<bool> WaitToDequeueAsync(CancellationToken cancellationToken) => new(false);

        public bool TryDequeue(out WorkEnvelope<string> item)
        {
            item = default;
            return false;
        }
    }
}
