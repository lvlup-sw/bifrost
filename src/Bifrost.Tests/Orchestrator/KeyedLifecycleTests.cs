// =============================================================================
// <copyright file="KeyedLifecycleTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;

using Bifrost.Core;
using Bifrost.Outcomes;
using Bifrost.Queues;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bifrost.Tests.Orchestrator;

/// <summary>
/// Gated orchestrator tests for keyed dispatch across shutdown and worker retirement: the abort
/// sweep against a handler still in its <c>finally</c> block, the sweep for every built-in
/// binding, drain of blocked items, the retired-worker drain, and wake hand-off races.
/// </summary>
/// <remarks>
/// Work items are strings of the form <c>key:n</c>. Handlers wait on
/// <see cref="TaskCompletionSource"/> gates, so each test decides when an item finishes. Bounded
/// waits only guard against hangs. The keyed queue is wrapped in <see cref="ObservedKeyedQueue"/>,
/// which counts dequeues, releases, and swept items, and signals checkpoints the tests await
/// instead of polling. A gate that holds handlers past cancellation is opened in a
/// <c>finally</c> block, so a failed assertion fails the test instead of hanging its disposal.
/// </remarks>
public sealed class KeyedLifecycleTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Abort sweeps queued and blocked items out as abandoned while the running handlers are still
    /// in their <c>finally</c> blocks. Each running item then releases its own slot exactly once,
    /// and every key is removed.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// The handlers ignore cancellation while they wait on the shared <c>finally</c> gate, so the
    /// gate is also opened in a <c>finally</c> block that runs before the orchestrator is disposed.
    /// Otherwise a failed assertion would leave both handlers blocked and disposal's abort would
    /// wait forever.
    /// </remarks>
    [Test]
    public async Task Abort_WhileHandlerInFinally_SweepsBlockedItems_AndReleasesEachSlotOnce()
    {
        var finallyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new GatedHandler { FinallyGate = finallyGate.Task };
        var records = new ConcurrentQueue<TerminalOutcomeRecord>();
        await using var orchestrator = CreateKeyed(handler, workers: 2, maxConcurrencyPerKey: 1, out var queue, records);
        try
        {
            Admit(orchestrator, "A:1", "B:1");
            await handler.WaitStartedAsync("A:1");
            await handler.WaitStartedAsync("B:1");
            Admit(orchestrator, "A:2", "A:3", "B:2");

            await Assert.That(orchestrator.Backlog).IsEqualTo(new DispatchBacklog(3, 0));

            var abort = orchestrator.AbortAsync();

            await Assert.That(queue.Discarded).IsEquivalentTo(new[] { "A:2", "A:3", "B:2" });
            await Assert.That(orchestrator.PendingCount).IsEqualTo(0);
            await Assert.That(orchestrator.Outcomes.Counters.Abandoned).IsEqualTo(3);
            await Assert.That(records.Count(r => r.Outcome == TerminalOutcome.Abandoned)).IsEqualTo(3);
            await Assert.That(queue.Inner.TrackedKeyCountForTest).IsEqualTo(2);

            await handler.WaitInFinallyAsync("A:1");
            await handler.WaitInFinallyAsync("B:1");

            await Assert.That(abort.IsCompleted).IsFalse();
            await Assert.That(queue.Releases).IsEqualTo(0);
            await Assert.That(queue.Inner.ActiveCountForTest("A")).IsEqualTo(1);

            finallyGate.TrySetResult();
            await abort.WaitAsync(WaitTimeout);

            await Assert.That(orchestrator.Completion.IsCompletedSuccessfully).IsTrue();
            await Assert.That(queue.Dequeued).IsEquivalentTo(new[] { "A:1", "B:1" });
            await Assert.That(queue.Releases).IsEqualTo(2);
            await Assert.That(queue.FailedReleases).IsEqualTo(0);
            await Assert.That(queue.Inner.TrackedKeyCountForTest).IsEqualTo(0);
            await Assert.That(orchestrator.Outcomes.Counters.Canceled).IsEqualTo(2);
            await Assert.That(records.Count).IsEqualTo(5);
        }
        finally
        {
            finallyGate.TrySetResult();
        }
    }

    /// <summary>
    /// The same sweep serves every built-in binding: abort removes the queued items and settles
    /// each one as abandoned, and the running item settles as canceled.
    /// </summary>
    /// <param name="strategy">The built-in dispatch strategy.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    [Arguments(DispatchStrategy.Fifo)]
    [Arguments(DispatchStrategy.PriorityExact)]
    [Arguments(DispatchStrategy.PriorityRelaxed)]
    public async Task Abort_BuiltInBinding_SweepsQueuedItemsAsAbandoned(DispatchStrategy strategy)
    {
        var handler = new GatedHandler();
        var records = new ConcurrentQueue<TerminalOutcomeRecord>();
        await using var orchestrator = new WorkOrchestrator<string>(
            handler,
            Options.Create(new WorkOrchestratorOptions
            {
                WorkerCount = 1,
                Capacity = 10,
                DispatchStrategy = strategy,
                FullMode = QueueFullMode.Reject,
            }),
            NullLogger<WorkOrchestrator<string>>.Instance);
        orchestrator.Outcomes.Observed = records.Enqueue;
        Admit(orchestrator, "R:1");
        await handler.WaitStartedAsync("R:1");
        Admit(orchestrator, "Q:1", "Q:2", "Q:3");

        await orchestrator.AbortAsync().WaitAsync(WaitTimeout);

        await Assert.That(orchestrator.PendingCount).IsEqualTo(0);
        await Assert.That(orchestrator.Backlog).IsEqualTo(new DispatchBacklog(0, 0));
        await Assert.That(orchestrator.Outcomes.Counters.Abandoned).IsEqualTo(3);
        await Assert.That(orchestrator.Outcomes.Counters.Canceled).IsEqualTo(1);
        await Assert.That(records.Count(r => r.Outcome == TerminalOutcome.Abandoned && r.Attempts == 0)).IsEqualTo(3);
        await Assert.That(handler.Starts()).IsEquivalentTo(new[] { "R:1" });
    }

    /// <summary>For a binding without blocked items, every pending item is reported as ready.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Backlog_FifoBinding_ReportsEveryPendingItemReady()
    {
        await using var orchestrator = new WorkOrchestrator<string>(
            new GatedHandler(),
            Options.Create(new WorkOrchestratorOptions { WorkerCount = 0, Capacity = 10 }),
            NullLogger<WorkOrchestrator<string>>.Instance);
        Admit(orchestrator, "A:1", "A:2");

        await Assert.That(orchestrator.Backlog).IsEqualTo(new DispatchBacklog(2, 2));
        await Assert.That(orchestrator.Backlog.Blocked).IsEqualTo(0);
    }

    /// <summary>
    /// Drain closes admission and still runs every accepted item, including items blocked behind a
    /// saturated key, as slots free up. Nothing is abandoned and every key is removed.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Drain_RunsBlockedItemsAsSlotsFree_AbandonsNothing()
    {
        var handler = new GatedHandler();
        await using var orchestrator = CreateKeyed(handler, workers: 3, maxConcurrencyPerKey: 1, out var queue);
        handler.OpenAllExcept("A:1");
        Admit(orchestrator, "A:1", "A:2", "A:3", "A:4", "B:1");
        await handler.WaitStartedAsync("A:1");
        await handler.WaitStartedAsync("B:1");

        var drain = orchestrator.DrainAsync();

        await Assert.That(orchestrator.Enqueue("C:1").Reason).IsEqualTo(RejectionReason.Shutdown);
        await Assert.That(drain.IsCompleted).IsFalse();
        await Assert.That(handler.HasStarted("A:2")).IsFalse();
        await Assert.That(orchestrator.Backlog).IsEqualTo(new DispatchBacklog(3, 0));

        handler.Open("A:1");
        await drain.WaitAsync(WaitTimeout);

        await Assert.That(string.Join(",", handler.Starts().Where(w => w.StartsWith("A:", StringComparison.Ordinal))))
            .IsEqualTo("A:1,A:2,A:3,A:4");
        await Assert.That(orchestrator.Outcomes.Counters.Succeeded).IsEqualTo(5);
        await Assert.That(orchestrator.Outcomes.Counters.Abandoned).IsEqualTo(0);
        await Assert.That(queue.Releases).IsEqualTo(5);
        await Assert.That(queue.DiscardCalls).IsEqualTo(0);
        await Assert.That(queue.Inner.TrackedKeyCountForTest).IsEqualTo(0);
    }

    /// <summary>
    /// When retirement stops the last worker while it runs a keyed item, the worker drains the
    /// retained keyed backlog through the leased path before it exits: blocked items run as their
    /// slots free up, and each lease is released once.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Retirement_LastWorker_DrainsRetainedKeyedBacklogWithLeases()
    {
        var handler = new GatedHandler();
        await using var orchestrator = CreateKeyed(handler, workers: 0, maxConcurrencyPerKey: 1, out var queue);
        using var retire = new CancellationTokenSource();
        var worker = orchestrator.CreateWorkerFunction(null, retire.Token)("retiring", CancellationToken.None);
        Admit(orchestrator, "A:1");
        await handler.WaitStartedAsync("A:1");
        Admit(orchestrator, "A:2", "A:3", "B:1");

        await retire.CancelAsync();
        handler.OpenAll();
        await worker.WaitAsync(WaitTimeout);

        await Assert.That(handler.Starts()).IsEquivalentTo(new[] { "A:1", "A:2", "A:3", "B:1" });
        await Assert.That(string.Join(",", handler.Starts().Where(w => w.StartsWith("A:", StringComparison.Ordinal))))
            .IsEqualTo("A:1,A:2,A:3");
        await Assert.That(queue.Releases).IsEqualTo(4);
        await Assert.That(queue.FailedReleases).IsEqualTo(0);
        await Assert.That(orchestrator.PendingCount).IsEqualTo(0);
        await Assert.That(orchestrator.ActiveWorkers).IsEqualTo(0);
        await Assert.That(queue.Inner.TrackedKeyCountForTest).IsEqualTo(0);

        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        await Assert.That(orchestrator.Outcomes.Counters.Abandoned).IsEqualTo(0);
    }

    /// <summary>
    /// A worker retired while it runs the only slot of a key leaves the blocked item behind that
    /// slot to a parked worker: the release funds a wake, the parked worker runs the item, and the
    /// retired worker exits without taking it.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Retirement_BusyWorker_BlockedItemRunsOnParkedWorker()
    {
        var handler = new GatedHandler();
        await using var orchestrator = CreateKeyed(handler, workers: 0, maxConcurrencyPerKey: 1, out var queue);
        using var retire = new CancellationTokenSource();
        var retiringBusy = 0;
        var survivorBusy = 0;
        var retiring = orchestrator.CreateWorkerFunction(
            busy => { if (busy) Interlocked.Increment(ref retiringBusy); }, retire.Token)("retiring", CancellationToken.None);
        Admit(orchestrator, "A:1");
        await handler.WaitStartedAsync("A:1");
        var survivor = orchestrator.CreateWorkerFunction(
            busy => { if (busy) Interlocked.Increment(ref survivorBusy); })("survivor", CancellationToken.None);
        await queue.WaitCallsReachedAsync(2).WaitAsync(WaitTimeout);
        Admit(orchestrator, "A:2");

        await Assert.That(orchestrator.Backlog).IsEqualTo(new DispatchBacklog(1, 0));

        await retire.CancelAsync();
        handler.OpenAll();
        await handler.WaitStartedAsync("A:2");
        await retiring.WaitAsync(WaitTimeout);
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        await survivor.WaitAsync(WaitTimeout);

        await Assert.That(Volatile.Read(ref retiringBusy)).IsEqualTo(1);
        await Assert.That(Volatile.Read(ref survivorBusy)).IsEqualTo(1);
        await Assert.That(queue.Releases).IsEqualTo(2);
        await Assert.That(queue.FailedReleases).IsEqualTo(0);
        await Assert.That(queue.Inner.TrackedKeyCountForTest).IsEqualTo(0);
    }

    /// <summary>
    /// Races a parked worker's retirement against an admission, many times. The item always runs
    /// on the surviving worker (no lost wake), and the surplus hand-off permit ends in at most a
    /// few misses: the survivor parks again instead of spinning.
    /// </summary>
    /// <remarks>
    /// The test uses no wall-clock windows. It waits until every permit is consumed, which means
    /// the survivor is parked, and then bounds the total miss count after the drain. A finite
    /// surplus can only cause as many misses as it has permits, while a spin would exceed the bound.
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Retirement_RacesAdmission_NoLostWake_SurplusPermitTolerated()
    {
        for (var round = 0; round < 20; round++)
        {
            var handler = new GatedHandler();
            handler.OpenAll();
            await using var orchestrator = CreateKeyed(handler, workers: 0, maxConcurrencyPerKey: 1, out var queue);
            using var retire = new CancellationTokenSource();
            var retiring = orchestrator.CreateWorkerFunction(null, retire.Token)("retiring", CancellationToken.None);
            var survivor = orchestrator.CreateWorkerFunction(null)("survivor", CancellationToken.None);
            await queue.WaitCallsReachedAsync(2).WaitAsync(WaitTimeout);

            using var start = new ManualResetEventSlim();
            var item = $"K{round}:1";
            var cancelTask = Task.Run(() =>
            {
                start.Wait();
                retire.Cancel();
            });
            var admitTask = Task.Run(() =>
            {
                start.Wait();
                Admit(orchestrator, item);
            });
            start.Set();
            await Task.WhenAll(cancelTask, admitTask).WaitAsync(WaitTimeout);

            await handler.WaitStartedAsync($"K{round}:1");
            await retiring.WaitAsync(WaitTimeout);
            await queue.ReleasesReachedAsync(1).WaitAsync(WaitTimeout);
            await queue.WakesConsumedAsync().WaitAsync(WaitTimeout);
            await Assert.That(queue.Releases).IsEqualTo(1);
            await Assert.That(survivor.IsCompleted).IsFalse();

            await orchestrator.DrainAsync().WaitAsync(WaitTimeout);
            await survivor.WaitAsync(WaitTimeout);
            await Assert.That(queue.Misses).IsLessThanOrEqualTo(2);
            await Assert.That(queue.FailedReleases).IsEqualTo(0);
            await Assert.That(queue.Inner.TrackedKeyCountForTest).IsEqualTo(0);
            await Assert.That(orchestrator.Outcomes.Counters.Succeeded).IsEqualTo(1);
        }
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

    /// <summary>
    /// A dynamic worker whose handler sees the abort's cancellation before the worker's
    /// retirement-linked dequeue token is cancelled still exits as a shutdown: the item is canceled
    /// and completion succeeds without a worker error.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// <para>
    /// A token source sets its cancelled flag before it runs its callbacks, and it runs them in
    /// reverse registration order. The handler registers a callback on its token after the worker
    /// linked its dequeue token, so that callback runs first. It lets the handler throw on another
    /// thread and holds the callback thread until the worker has left its active count, which
    /// happens after the worker classified the exception. So the dequeue token is still uncancelled
    /// when the worker classifies it.
    /// </para>
    /// <para>
    /// The callback waits for the active count, not for the worker task, because the worker
    /// disposes its linked token source on exit, and that waits for the running callback. For the
    /// same reason the handler does not dispose its registration.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Abort_HandlerSeesCancelBeforeDequeueToken_CompletesWithoutWorkerError()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkOrchestrator<string>? holder = null;
        var handler = new DelegateHandler(async (_, ct) =>
        {
            ct.Register(() =>
            {
                proceed.TrySetResult();
                SpinWait.SpinUntil(() => Volatile.Read(ref holder)!.ActiveWorkers == 0, WaitTimeout);
            });
            started.TrySetResult();
            await proceed.Task.WaitAsync(WaitTimeout);
            ct.ThrowIfCancellationRequested();
        });
        await using var orchestrator = CreateKeyed(handler, workers: 0, maxConcurrencyPerKey: 1, out _);
        Volatile.Write(ref holder, orchestrator);
        using var retire = new CancellationTokenSource();
        var worker = orchestrator.CreateWorkerFunction(null, retire.Token)("dynamic", CancellationToken.None);
        Admit(orchestrator, "A:1");
        await started.Task.WaitAsync(WaitTimeout);

        await orchestrator.AbortAsync().WaitAsync(WaitTimeout);
        await worker.WaitAsync(WaitTimeout);

        await Assert.That(orchestrator.Completion.IsCompletedSuccessfully).IsTrue();
        await Assert.That(orchestrator.Outcomes.Counters.Canceled).IsEqualTo(1);
    }

    private static WorkOrchestrator<string> CreateKeyed(IWorkHandler<string> handler, int workers, int maxConcurrencyPerKey,
        out ObservedKeyedQueue queue, ConcurrentQueue<TerminalOutcomeRecord>? records = null)
    {
        var observed = new ObservedKeyedQueue(KeyedWorkQueue.Create<string, string>(KeyOf, 100, 100, maxConcurrencyPerKey));
        queue = observed;
        var orchestrator = new WorkOrchestrator<string>(
            handler,
            Options.Create(new WorkOrchestratorOptions
            {
                WorkerCount = workers,
                Capacity = 100,
                FullMode = QueueFullMode.Reject,
                DispatchStrategy = DispatchStrategy.Keyed,
            }),
            NullLogger<WorkOrchestrator<string>>.Instance,
            null,
            null,
            (_, _) => observed,
            new TerminalOutcomeOptions<string>());
        if (records is not null) orchestrator.Outcomes.Observed = records.Enqueue;
        return orchestrator;
    }

    /// <summary>
    /// A handler whose items wait on per-item gates. An optional shared gate holds every item in
    /// its <c>finally</c> block, after its cancellation and before it exits.
    /// </summary>
    private sealed class DelegateHandler(Func<string, CancellationToken, ValueTask> body) : IWorkHandler<string>
    {
        public ValueTask HandleAsync(string work, CancellationToken ct) => body(work, ct);
    }

    private sealed class GatedHandler : IWorkHandler<string>
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, TaskCompletionSource> _started = [];
        private readonly Dictionary<string, TaskCompletionSource> _inFinally = [];
        private readonly Dictionary<string, TaskCompletionSource> _gates = [];
        private readonly List<string> _starts = [];
        private bool _openAll;
        private string? _except;

        public Task? FinallyGate { get; init; }

        public async ValueTask HandleAsync(string work, CancellationToken ct)
        {
            TaskCompletionSource gate;
            lock (_gate)
            {
                _starts.Add(work);
                gate = GateFor(work);
                SignalFor(_started, work).TrySetResult();
            }

            try
            {
                await gate.Task.WaitAsync(ct);
            }
            finally
            {
                lock (_gate) SignalFor(_inFinally, work).TrySetResult();
                if (FinallyGate is { } hold) await hold;
            }
        }

        public Task WaitStartedAsync(string work)
        {
            lock (_gate) return SignalFor(_started, work).Task.WaitAsync(WaitTimeout);
        }

        public Task WaitInFinallyAsync(string work)
        {
            lock (_gate) return SignalFor(_inFinally, work).Task.WaitAsync(WaitTimeout);
        }

        public bool HasStarted(string work)
        {
            lock (_gate) return _starts.Contains(work);
        }

        public string[] Starts()
        {
            lock (_gate) return [.. _starts];
        }

        public void Open(string work)
        {
            lock (_gate) GateFor(work).TrySetResult();
        }

        public void OpenAll() => OpenAllExcept(null);

        public void OpenAllExcept(string? except)
        {
            lock (_gate)
            {
                _openAll = true;
                _except = except;
                foreach (var (work, gate) in _gates)
                {
                    if (work != except) gate.TrySetResult();
                }
            }
        }

        private TaskCompletionSource GateFor(string work)
        {
            if (!_gates.TryGetValue(work, out var gate))
            {
                gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_openAll && work != _except) gate.TrySetResult();
                _gates[work] = gate;
            }

            return gate;
        }

        private static TaskCompletionSource SignalFor(Dictionary<string, TaskCompletionSource> signals, string work)
        {
            if (!signals.TryGetValue(work, out var signal))
            {
                signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                signals[work] = signal;
            }

            return signal;
        }
    }
}
