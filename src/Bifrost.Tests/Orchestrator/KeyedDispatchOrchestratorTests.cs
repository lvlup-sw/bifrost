// =============================================================================
// <copyright file="KeyedDispatchOrchestratorTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.CodeAnalysis;

using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.DeadLetter;
using Bifrost.Queues;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bifrost.Tests.Orchestrator;

/// <summary>
/// Orchestrator tests for keyed dispatch through the internal queue-factory constructor: per-key
/// order and concurrency, progress for other keys beside a saturated key, admission limits, idle
/// key removal, drain with a blocked-only backlog, and slot release after failures and retries.
/// </summary>
/// <remarks>
/// Work items are strings of the form <c>key:n</c>. Handlers are gated with
/// <see cref="TaskCompletionSource"/> per item, so each test decides when an item finishes.
/// Bounded waits only guard against hangs. Queue counters are read while handlers are gated,
/// when the queue state cannot change.
/// </remarks>
public sealed class KeyedDispatchOrchestratorTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>With one slot per key, a key's items run one at a time in admission order.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ConcurrencyOne_RunsKeyInAdmissionOrder()
    {
        var handler = new GatedHandler();
        await using var orchestrator = CreateKeyed(handler, workers: 3, maxConcurrencyPerKey: 1, out var queue);
        Admit(orchestrator, "A:1", "A:2", "A:3");

        await handler.WaitStartedAsync("A:1");
        await Assert.That(queue.ActiveCountForTest("A")).IsEqualTo(1);
        await Assert.That(orchestrator.PendingCount).IsEqualTo(2);
        await Assert.That(queue.DispatchableCount).IsEqualTo(0);

        handler.Open("A:1");
        await handler.WaitStartedAsync("A:2");
        await Assert.That(orchestrator.PendingCount).IsEqualTo(1);

        handler.Open("A:2");
        await handler.WaitStartedAsync("A:3");
        handler.Open("A:3");
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);

        await Assert.That(string.Join(",", handler.Starts())).IsEqualTo("A:1,A:2,A:3");
        await Assert.That(handler.MaxRunning("A")).IsEqualTo(1);
    }

    /// <summary>With two slots per key, items still leave the key's queue in admission order.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ConcurrencyTwo_KeepsDequeueOrder()
    {
        var handler = new GatedHandler();
        await using var orchestrator = CreateKeyed(handler, workers: 3, maxConcurrencyPerKey: 2, out var queue);
        Admit(orchestrator, "A:1", "A:2", "A:3", "A:4");

        await handler.WaitStartedAsync("A:1");
        await handler.WaitStartedAsync("A:2");
        await Assert.That(queue.ActiveCountForTest("A")).IsEqualTo(2);
        await Assert.That(orchestrator.PendingCount).IsEqualTo(2);

        handler.Open("A:2");
        await handler.WaitStartedAsync("A:3");
        await Assert.That(handler.HasStarted("A:4")).IsFalse();

        handler.Open("A:1");
        await handler.WaitStartedAsync("A:4");
        handler.Open("A:3");
        handler.Open("A:4");
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);

        await Assert.That(handler.MaxRunning("A")).IsEqualTo(2);
        await Assert.That(string.Join(",", handler.Starts().Skip(2))).IsEqualTo("A:3,A:4");
    }

    /// <summary>A saturated key leaves a free worker for another key's later item.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task SaturatedKey_LeavesFreeWorkerForOtherKey()
    {
        var handler = new GatedHandler();
        await using var orchestrator = CreateKeyed(handler, workers: 2, maxConcurrencyPerKey: 1, out var queue);
        Admit(orchestrator, "A:1", "A:2", "A:3");
        await handler.WaitStartedAsync("A:1");

        Admit(orchestrator, "B:1");

        await handler.WaitStartedAsync("B:1");
        await Assert.That(handler.HasStarted("A:2")).IsFalse();
        await Assert.That(queue.ActiveCountForTest("A")).IsEqualTo(1);
        await Assert.That(orchestrator.PendingCount).IsEqualTo(2);

        handler.OpenAll();
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        await Assert.That(string.Join(",", handler.Starts().Where(static w => w.StartsWith('A')))).IsEqualTo("A:1,A:2,A:3");
    }

    /// <summary>
    /// The per-key limit rejects with <see cref="RejectionReason.KeyLimitExceeded"/>, the global
    /// limit rejects with <see cref="RejectionReason.CapacityExceeded"/>, and the global limit wins.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AdmissionLimits_GlobalWinsOverPerKey()
    {
        var handler = new GatedHandler();
        await using var orchestrator = CreateKeyed(handler, workers: 0, maxConcurrencyPerKey: 1, out _, capacity: 3, maxPendingPerKey: 2);

        await Assert.That(orchestrator.Enqueue("A:1").IsAccepted).IsTrue();
        await Assert.That((await orchestrator.EnqueueAsync("A:2")).IsAccepted).IsTrue();
        await Assert.That(orchestrator.Enqueue("A:3").Reason).IsEqualTo(RejectionReason.KeyLimitExceeded);
        await Assert.That((await orchestrator.EnqueueAsync("A:3")).Reason).IsEqualTo(RejectionReason.KeyLimitExceeded);
        await Assert.That(orchestrator.TryEnqueue("B:1")).IsTrue();

        await Assert.That(orchestrator.Enqueue("A:4").Reason).IsEqualTo(RejectionReason.CapacityExceeded);
        await Assert.That((await orchestrator.EnqueueAsync("C:1")).Reason).IsEqualTo(RejectionReason.CapacityExceeded);
        await Assert.That(orchestrator.PendingCount).IsEqualTo(3);

        orchestrator.Complete();
        await Assert.That(orchestrator.Enqueue("C:1").Reason).IsEqualTo(RejectionReason.Shutdown);
    }

    /// <summary>A key whose items have all finished is removed before the worker reports idle.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task FinishedKeys_AreRemovedBeforeIdleReport()
    {
        var handler = new GatedHandler();
        await using var orchestrator = CreateKeyed(handler, workers: 0, maxConcurrencyPerKey: 1, out var queue);
        var trackedAtIdle = new List<int>();
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Admit(orchestrator, "A:1", "B:1");

        var run = orchestrator.CreateWorkerFunction(busy =>
        {
            if (busy) return;
            lock (trackedAtIdle)
            {
                trackedAtIdle.Add(queue.TrackedKeyCountForTest);
                if (trackedAtIdle.Count == 2) idle.TrySetResult();
            }
        })("W", CancellationToken.None);
        handler.OpenAll();
        await idle.Task.WaitAsync(WaitTimeout);

        await Assert.That(string.Join(",", trackedAtIdle)).IsEqualTo("1,0");
        await Assert.That(queue.TrackedKeyCountForTest).IsEqualTo(0);
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        await run.WaitAsync(WaitTimeout);
    }

    /// <summary>
    /// A drain with a blocked-only backlog neither hangs nor spins: idle workers stay parked until
    /// a release makes the next item dispatchable, and the drain finishes once all items ran.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Drain_WithBlockedOnlyBacklog_ParksWithoutSpinAndFinishes()
    {
        var handler = new GatedHandler();
        var counting = new CountingQueue(KeyedWorkQueue.Create<string, string>(KeyOf, 100, 100, 1));
        await using var orchestrator = CreateWithQueue(handler, workers: 3, counting);
        Admit(orchestrator, "A:1", "A:2", "A:3");
        await handler.WaitStartedAsync("A:1");
        await counting.WaitForWaitCallsAsync(3).WaitAsync(WaitTimeout);

        var drain = orchestrator.DrainAsync();
        var before = counting.Snapshot();
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        var after = counting.Snapshot();

        await Assert.That(drain.IsCompleted).IsFalse();
        await Assert.That(after).IsEqualTo(before);
        await Assert.That(after.Misses).IsEqualTo(0);

        handler.Open("A:1");
        await handler.WaitStartedAsync("A:2");
        handler.Open("A:2");
        await handler.WaitStartedAsync("A:3");
        await Assert.That(drain.IsCompleted).IsFalse();
        handler.Open("A:3");

        await drain.WaitAsync(WaitTimeout);
        await Assert.That(string.Join(",", handler.Starts())).IsEqualTo("A:1,A:2,A:3");
        await Assert.That(counting.Inner.TrackedKeyCountForTest).IsEqualTo(0);
        await Assert.That(orchestrator.ActiveWorkers).IsEqualTo(0);
    }

    /// <summary>
    /// The permit funded by a release wakes a parked worker when the releasing worker cannot take
    /// the newly dispatchable item itself.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// The releasing worker is held in its idle report, which runs after the key-slot release and
    /// before its greedy inner loop. So the only way the key's next item can start is the permit
    /// the release funds, and the parked second worker must take it.
    /// </remarks>
    [Test]
    public async Task ReleasePermit_WakesParkedWorker_WhenReleasingWorkerIsHeld()
    {
        var handler = new GatedHandler();
        var counting = new CountingQueue(KeyedWorkQueue.Create<string, string>(KeyOf, 100, 100, 1));
        await using var orchestrator = CreateWithQueue(handler, workers: 0, counting);
        using var holdIdle = new ManualResetEventSlim(false);
        var idleEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldOnce = 0;
        Admit(orchestrator, "A:1", "A:2");

        var held = orchestrator.CreateWorkerFunction(busy =>
        {
            if (busy || Interlocked.Exchange(ref heldOnce, 1) == 1) return;
            idleEntered.TrySetResult();
            holdIdle.Wait(WaitTimeout);
        })("held", CancellationToken.None);
        await handler.WaitStartedAsync("A:1");
        var parked = orchestrator.CreateWorkerFunction()("parked", CancellationToken.None);
        await counting.WaitForWaitCallsAsync(2).WaitAsync(WaitTimeout);

        handler.Open("A:1");
        await idleEntered.Task.WaitAsync(WaitTimeout);
        await handler.WaitStartedAsync("A:2");

        await Assert.That(holdIdle.IsSet).IsFalse();
        holdIdle.Set();
        handler.Open("A:2");
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        await held.WaitAsync(WaitTimeout);
        await parked.WaitAsync(WaitTimeout);
        await Assert.That(string.Join(",", handler.Starts())).IsEqualTo("A:1,A:2");
    }

    /// <summary>A throwing handler still frees its key slot, so the key's next item runs.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ThrowingHandler_ReleasesKeySlot()
    {
        var handler = new GatedHandler();
        await using var orchestrator = CreateKeyed(handler, workers: 2, maxConcurrencyPerKey: 1, out var queue);
        Admit(orchestrator, "A:1", "A:2");
        await handler.WaitStartedAsync("A:1");

        handler.Fail("A:1", new InvalidOperationException("boom"));

        await handler.WaitStartedAsync("A:2");
        handler.Open("A:2");
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        await Assert.That(queue.TrackedKeyCountForTest).IsEqualTo(0);
        await Assert.That(handler.MaxRunning("A")).IsEqualTo(1);
    }

    /// <summary>
    /// The key slot covers every dead-letter retry: the key's next item starts only after the last
    /// attempt, although another worker is free.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DeadLetterRetries_HoldKeySlotUntilLastAttempt()
    {
        var gated = new GatedHandler();
        var attempts = 0;
        var events = new List<string>();
        var inner = new DelegateHandler(async (work, ct) =>
        {
            if (work != "A:1")
            {
                lock (events) events.Add($"Run:{work}");
                return;
            }

            var attempt = Interlocked.Increment(ref attempts);
            lock (events) events.Add($"Attempt:{attempt}");
            if (attempt == 1) await gated.HandleAsync(work, ct);
            throw new InvalidOperationException("always fails");
        });
        var dlqOptions = Options.Create(new DeadLetterQueueOptions { MaxRetries = 2 });
        var dlq = new DeadLetterQueue<string>(dlqOptions, NullLogger<DeadLetterQueue<string>>.Instance);
        var handler = new DeadLetterHandler<string>(
            inner,
            dlq,
            new DeadLetterNotifier<string>(NullLogger<DeadLetterNotifier<string>>.Instance),
            dlqOptions,
            NullLogger<DeadLetterHandler<string>>.Instance);
        await using var orchestrator = CreateKeyed(handler, workers: 2, maxConcurrencyPerKey: 1, out var queue);
        Admit(orchestrator, "A:1", "A:2");
        await gated.WaitStartedAsync("A:1");
        await Assert.That(queue.ActiveCountForTest("A")).IsEqualTo(1);
        await Assert.That(orchestrator.PendingCount).IsEqualTo(1);

        gated.Open("A:1");
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);

        await Assert.That(string.Join(",", events)).IsEqualTo("Attempt:1,Attempt:2,Attempt:3,Run:A:2");
        await Assert.That(dlq.Count).IsEqualTo(1);
        await Assert.That(queue.TrackedKeyCountForTest).IsEqualTo(0);
    }

    /// <summary>A keyed queue with Wait full mode is rejected at construction and disposed.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task FactoryConstructor_KeyedQueueWithWaitMode_Throws()
    {
        var queue = KeyedWorkQueue.Create<string, string>(KeyOf, 10, 10, 1);
        var error = await Assert.That(() => new WorkOrchestrator<string>(
                new GatedHandler(),
                Options.Create(new WorkOrchestratorOptions { WorkerCount = 0, Capacity = 10, FullMode = QueueFullMode.Wait }),
                NullLogger<WorkOrchestrator<string>>.Instance,
                null,
                null,
                (_, _) => queue))
            .Throws<ArgumentException>();

        await Assert.That(error!.Message).Contains("Keyed dispatch supports only Reject full mode.");
        await Assert.That(queue.IsDisposedForTest).IsTrue();
    }

    /// <summary>A keyed queue combined with class priority dispatch is rejected at construction.</summary>
    /// <param name="strategy">The priority strategy under test.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    [Arguments(DispatchStrategy.PriorityExact)]
    [Arguments(DispatchStrategy.PriorityRelaxed)]
    public async Task FactoryConstructor_KeyedQueueWithPriority_Throws(DispatchStrategy strategy)
    {
        var queue = KeyedWorkQueue.Create<string, string>(KeyOf, 10, 10, 1);
        var error = await Assert.That(() => new WorkOrchestrator<string>(
                new GatedHandler(),
                Options.Create(new WorkOrchestratorOptions
                {
                    WorkerCount = 0,
                    Capacity = 10,
                    FullMode = QueueFullMode.Reject,
                    DispatchStrategy = strategy,
                }),
                NullLogger<WorkOrchestrator<string>>.Instance,
                null,
                null,
                (_, _) => queue))
            .Throws<ArgumentException>();

        await Assert.That(error!.Message).Contains("Keyed dispatch cannot be combined with class priority dispatch.");
        await Assert.That(queue.IsDisposedForTest).IsTrue();
    }

    /// <summary>The orchestrator owns the keyed queue and disposes it.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Dispose_DisposesKeyedQueue()
    {
        var orchestrator = CreateKeyed(new GatedHandler(), workers: 1, maxConcurrencyPerKey: 1, out var queue);
        await orchestrator.DisposeAsync();

        await Assert.That(queue.IsDisposedForTest).IsTrue();
    }

    private static string KeyOf(string work) => work.Split(':')[0];

    private static void Admit(WorkOrchestrator<string> orchestrator, params string[] work)
    {
        foreach (var item in work)
        {
            if (!orchestrator.Enqueue(item).IsAccepted)
                throw new InvalidOperationException($"Setup admission of {item} was rejected.");
        }
    }

    private static WorkOrchestrator<string> CreateKeyed(IWorkHandler<string> handler, int workers, int maxConcurrencyPerKey,
        out KeyedWorkQueue<string, string> queue, int capacity = 100, int maxPendingPerKey = 100)
    {
        var created = KeyedWorkQueue.Create<string, string>(KeyOf, capacity, maxPendingPerKey, maxConcurrencyPerKey);
        queue = created;
        return new WorkOrchestrator<string>(
            handler,
            Options.Create(new WorkOrchestratorOptions { WorkerCount = workers, Capacity = capacity, FullMode = QueueFullMode.Reject }),
            NullLogger<WorkOrchestrator<string>>.Instance,
            null,
            null,
            (_, _) => created);
    }

    private static WorkOrchestrator<string> CreateWithQueue(IWorkHandler<string> handler, int workers, CountingQueue queue)
        => new(
            handler,
            Options.Create(new WorkOrchestratorOptions { WorkerCount = workers, Capacity = 100, FullMode = QueueFullMode.Reject }),
            NullLogger<WorkOrchestrator<string>>.Instance,
            null,
            null,
            (_, _) => queue);

    private sealed class DelegateHandler(Func<string, CancellationToken, ValueTask> body) : IWorkHandler<string>
    {
        public ValueTask HandleAsync(string work, CancellationToken ct) => body(work, ct);
    }

    private sealed class GatedHandler : IWorkHandler<string>
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, TaskCompletionSource> _started = [];
        private readonly Dictionary<string, TaskCompletionSource> _gates = [];
        private readonly Dictionary<string, int> _running = [];
        private readonly Dictionary<string, int> _maxRunning = [];
        private readonly List<string> _starts = [];
        private bool _openAll;

        public async ValueTask HandleAsync(string work, CancellationToken ct)
        {
            var key = KeyOf(work);
            TaskCompletionSource gate;
            lock (_gate)
            {
                var running = _running.GetValueOrDefault(key) + 1;
                _running[key] = running;
                _maxRunning[key] = Math.Max(_maxRunning.GetValueOrDefault(key), running);
                _starts.Add(work);
                gate = GateFor(work);
                SignalFor(work).TrySetResult();
            }

            try
            {
                await gate.Task.WaitAsync(ct);
            }
            finally
            {
                lock (_gate) _running[key]--;
            }
        }

        public Task WaitStartedAsync(string work)
        {
            lock (_gate) return SignalFor(work).Task.WaitAsync(WaitTimeout);
        }

        public bool HasStarted(string work)
        {
            lock (_gate) return _starts.Contains(work);
        }

        public string[] Starts()
        {
            lock (_gate) return [.. _starts];
        }

        public int MaxRunning(string key)
        {
            lock (_gate) return _maxRunning.GetValueOrDefault(key);
        }

        public void Open(string work)
        {
            lock (_gate) GateFor(work).TrySetResult();
        }

        public void Fail(string work, Exception error)
        {
            lock (_gate) GateFor(work).TrySetException(error);
        }

        public void OpenAll()
        {
            lock (_gate)
            {
                _openAll = true;
                foreach (var gate in _gates.Values) gate.TrySetResult();
            }
        }

        private TaskCompletionSource GateFor(string work)
        {
            if (!_gates.TryGetValue(work, out var gate))
            {
                gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_openAll) gate.TrySetResult();
                _gates[work] = gate;
            }

            return gate;
        }

        private TaskCompletionSource SignalFor(string work)
        {
            if (!_started.TryGetValue(work, out var signal))
            {
                signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _started[work] = signal;
            }

            return signal;
        }
    }

    private sealed class CountingQueue(KeyedWorkQueue<string, string> inner)
        : IWorkQueue<WorkEnvelope<string>>, ILeasedWorkQueue<WorkEnvelope<string>>, IWorkQueueLifecycle, IDisposable
    {
        private int _waitCalls;
        private int _misses;

        public KeyedWorkQueue<string, string> Inner => inner;

        public int Count => inner.Count;

        public (int WaitCalls, int Misses) Snapshot() => (Volatile.Read(ref _waitCalls), Volatile.Read(ref _misses));

        public async Task WaitForWaitCallsAsync(int expected)
        {
            while (Volatile.Read(ref _waitCalls) < expected) await Task.Delay(5);
        }

        public EnqueueResult Enqueue(in WorkEnvelope<string> item) => inner.Enqueue(item);

        public bool TryEnqueue(in WorkEnvelope<string> item) => inner.TryEnqueue(item);

        public ValueTask<bool> WaitToDequeueAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _waitCalls);
            return inner.WaitToDequeueAsync(cancellationToken);
        }

        public bool TryDequeue([MaybeNullWhen(false)] out WorkEnvelope<string> item)
            => ((IWorkQueue<WorkEnvelope<string>>)inner).TryDequeue(out item);

        public bool TryDequeue([MaybeNullWhen(false)] out WorkEnvelope<string> item, out object? lease)
        {
            var took = inner.TryDequeue(out item, out lease);
            if (!took) Interlocked.Increment(ref _misses);
            return took;
        }

        public void Release(object lease) => inner.Release(lease);

        public void Complete() => inner.Complete();

        public void WakeConsumer() => inner.WakeConsumer();

        public void ConsumeMatchedWake() => inner.ConsumeMatchedWake();

        public void Dispose() => inner.Dispose();
    }
}
