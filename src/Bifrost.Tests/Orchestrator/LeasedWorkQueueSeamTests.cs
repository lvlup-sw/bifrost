// =============================================================================
// <copyright file="LeasedWorkQueueSeamTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.CodeAnalysis;

using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.DeadLetter;
using Bifrost.Handlers;
using Bifrost.Outcomes;
using Bifrost.Queues;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bifrost.Tests.Orchestrator;

/// <summary>
/// Tests for the leased half of the internal dispatch-queue seam: when the injected queue
/// implements <see cref="ILeasedWorkQueue{T}"/>, the worker loop dequeues through it and releases
/// each lease exactly once, after the handler pipeline exits and before the worker reports idle.
/// </summary>
/// <remarks>
/// <para>
/// The injected queue is a recording fake: FIFO order, a distinct lease object per dequeued item,
/// and one ordered event log shared with the handler, the state callback, and any scoped probe.
/// Its plain <see cref="IWorkQueue{T}.TryDequeue(out T)"/> throws, so a worker that skips the
/// leased path faults the orchestrator. It throws on an unknown or a repeated release.
/// </para>
/// <para>
/// The orchestrators start with no workers, and each test starts one worker by hand. Handlers are
/// gated with <see cref="TaskCompletionSource"/>, and every wait uses a timeout only as a hang guard.
/// </para>
/// </remarks>
public sealed class LeasedWorkQueueSeamTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A handled item is released once, after its handler returns and before the worker reports idle.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Success_ReleasesOncePerItem_AfterHandlerAndBeforeIdle()
    {
        var log = new EventLog();
        var queue = new RecordingLeasedQueue(log);
        await using var orchestrator = CreateWithQueue(new LoggingHandler(log), queue);
        await Assert.That(orchestrator.Enqueue("a").IsAccepted).IsTrue();
        await Assert.That(orchestrator.Enqueue("b").IsAccepted).IsTrue();

        var run = orchestrator.CreateWorkerFunction(busy => log.Add(busy ? "Busy" : "Idle"))("W", CancellationToken.None);
        await log.WaitForCountAsync("Idle", 2).WaitAsync(WaitTimeout);
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        await run.WaitAsync(WaitTimeout);

        await Assert.That(Sequence(log.Snapshot())).IsEqualTo(Sequence(new[]
            {
                "Enqueue:a", "Enqueue:b",
                "Dequeue:a", "Busy", "Handled:a", "Release:a", "Idle",
                "Dequeue:b", "ConsumeMatchedWake", "Busy", "Handled:b", "Release:b", "Idle",
                "Complete",
            }));
        await Assert.That(queue.OutstandingLeases).IsEqualTo(0);
    }

    /// <summary>A handler failure still releases the lease once, and the worker takes the next item.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task HandlerThrows_ReleasesOnce_AndWorkerContinues()
    {
        var log = new EventLog();
        var queue = new RecordingLeasedQueue(log);
        var handler = new LoggingHandler(log, (work, _) =>
            work == "bad" ? ValueTask.FromException(new InvalidOperationException("boom")) : ValueTask.CompletedTask);
        await using var orchestrator = CreateWithQueue(handler, queue);
        await Assert.That(orchestrator.Enqueue("bad").IsAccepted).IsTrue();
        await Assert.That(orchestrator.Enqueue("good").IsAccepted).IsTrue();

        var run = orchestrator.CreateWorkerFunction(busy => log.Add(busy ? "Busy" : "Idle"))("W", CancellationToken.None);
        await log.WaitForCountAsync("Idle", 2).WaitAsync(WaitTimeout);
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        await run.WaitAsync(WaitTimeout);

        var events = log.Snapshot();
        await Assert.That(events.Count(static e => e == "Release:bad")).IsEqualTo(1);
        await Assert.That(events.Count(static e => e == "Release:good")).IsEqualTo(1);
        await Assert.That(events).Contains("Handled:good");
        await Assert.That(Array.IndexOf(events, "Release:bad")).IsLessThan(Array.IndexOf(events, "Dequeue:good"));
        await Assert.That(queue.OutstandingLeases).IsEqualTo(0);
    }

    /// <summary>An abort during a running handler still releases the lease once.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AbortDuringRunningHandler_ReleasesOnce()
    {
        var log = new EventLog();
        var queue = new RecordingLeasedQueue(log);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new LoggingHandler(log, async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });
        await using var orchestrator = CreateWithQueue(handler, queue);
        await Assert.That(orchestrator.Enqueue("a").IsAccepted).IsTrue();

        var run = orchestrator.CreateWorkerFunction(busy => log.Add(busy ? "Busy" : "Idle"))("W", CancellationToken.None);
        await started.Task.WaitAsync(WaitTimeout);
        await orchestrator.AbortAsync().WaitAsync(WaitTimeout);
        await run.WaitAsync(WaitTimeout);

        var events = log.Snapshot();
        await Assert.That(events.Count(static e => e == "Release:a")).IsEqualTo(1);
        await Assert.That(events).DoesNotContain("Handled:a");
        await Assert.That(Array.IndexOf(events, "Release:a")).IsLessThan(Array.LastIndexOf(events, "Idle"));
        await Assert.That(queue.OutstandingLeases).IsEqualTo(0);
    }

    /// <summary>A scoped handler's DI scope is disposed before the lease is released.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ScopedHandler_DisposesScopeBeforeRelease()
    {
        var log = new EventLog();
        var queue = new RecordingLeasedQueue(log);
        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddScoped<ScopeProbe>();
        services.AddScoped<IWorkHandler<string>, ProbeHandler>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var proxy = new ScopedHandlerProxy<string>(provider.GetRequiredService<IServiceScopeFactory>());
        await using var orchestrator = CreateWithQueue(proxy, queue);
        await Assert.That(orchestrator.Enqueue("a").IsAccepted).IsTrue();

        var run = orchestrator.CreateWorkerFunction(busy => log.Add(busy ? "Busy" : "Idle"))("W", CancellationToken.None);
        await log.WaitForCountAsync("Idle", 1).WaitAsync(WaitTimeout);
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        await run.WaitAsync(WaitTimeout);

        await Assert.That(Sequence(log.Snapshot())).IsEqualTo(Sequence(new[] { "Enqueue:a", "Dequeue:a", "Busy", "Handled:a", "ScopeDisposed", "Release:a", "Idle", "Complete" }));
    }

    /// <summary>
    /// Dead-letter retries run inside one lease: three attempts, then exactly one release, then
    /// one capture, then the idle report.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DeadLetterRetries_ReleaseOnceAfterLastAttempt()
    {
        var log = new EventLog();
        var queue = new RecordingLeasedQueue(log);
        var inner = new LoggingHandler(log, (work, _) =>
        {
            log.Add($"Attempt:{work}");
            return ValueTask.FromException(new InvalidOperationException("always fails"));
        });
        var dlqOptions = Options.Create(new DeadLetterQueueOptions { MaxRetries = 2 });
        var dlq = new DeadLetterQueue<string>(dlqOptions, NullLogger<DeadLetterQueue<string>>.Instance);
        var outcomes = new TerminalOutcomeOptions<string>
        {
            DeadLetterQueue = new LoggingDeadLetterQueue(log, dlq),
            DeadLetterRetries = dlqOptions.Value.MaxRetries,
        };
        await using var orchestrator = CreateWithQueue(inner, queue, outcomes);
        await Assert.That(orchestrator.Enqueue("a").IsAccepted).IsTrue();

        var run = orchestrator.CreateWorkerFunction(busy => log.Add(busy ? "Busy" : "Idle"))("W", CancellationToken.None);
        await log.WaitForCountAsync("Idle", 1).WaitAsync(WaitTimeout);
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        await run.WaitAsync(WaitTimeout);

        await Assert.That(Sequence(log.Snapshot())).IsEqualTo(Sequence(new[] { "Enqueue:a", "Dequeue:a", "Busy", "Attempt:a", "Attempt:a", "Attempt:a", "Release:a", "Captured:a", "Idle", "Complete" }));
        await Assert.That(dlq.Count).IsEqualTo(1);
    }

    /// <summary>
    /// The greedy inner loop dequeues through the lease path and takes one matched wake per extra item.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task GreedyInnerLoop_ReleasesEachItem_AndConsumesMatchedWakes()
    {
        var log = new EventLog();
        var queue = new RecordingLeasedQueue(log);
        await using var orchestrator = CreateWithQueue(new LoggingHandler(log), queue);
        await Assert.That(orchestrator.Enqueue("a").IsAccepted).IsTrue();
        await Assert.That(orchestrator.Enqueue("b").IsAccepted).IsTrue();
        await Assert.That(orchestrator.Enqueue("c").IsAccepted).IsTrue();

        var run = orchestrator.CreateWorkerFunction(busy => log.Add(busy ? "Busy" : "Idle"))("W", CancellationToken.None);
        await log.WaitForCountAsync("Idle", 3).WaitAsync(WaitTimeout);
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        await run.WaitAsync(WaitTimeout);

        var events = log.Snapshot();
        await Assert.That(Sequence(events.Where(static e => e.StartsWith("Release:", StringComparison.Ordinal)).ToArray())).IsEqualTo(Sequence(new[] { "Release:a", "Release:b", "Release:c" }));
        await Assert.That(events.Count(static e => e == "ConsumeMatchedWake")).IsEqualTo(2);
        await Assert.That(queue.OutstandingLeases).IsEqualTo(0);
    }

    /// <summary>
    /// A retired last worker drains the items still queued through the lease path and releases each.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task RetiredLastWorker_DrainsRetainedItems_WithLeases()
    {
        var log = new EventLog();
        var queue = new RecordingLeasedQueue(log);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new LoggingHandler(log, async (work, ct) =>
        {
            if (work != "a") return;
            started.TrySetResult();
            await gate.Task.WaitAsync(ct);
        });
        await using var orchestrator = CreateWithQueue(handler, queue);
        using var retirement = new CancellationTokenSource();
        await Assert.That(orchestrator.Enqueue("a").IsAccepted).IsTrue();
        await Assert.That(orchestrator.Enqueue("b").IsAccepted).IsTrue();

        var run = orchestrator.CreateWorkerFunction(busy => log.Add(busy ? "Busy" : "Idle"), retirement.Token)("W", CancellationToken.None);
        await started.Task.WaitAsync(WaitTimeout);
        await retirement.CancelAsync();
        gate.SetResult();
        await run.WaitAsync(WaitTimeout);

        var events = log.Snapshot();
        await Assert.That(events.Count(static e => e == "Release:a")).IsEqualTo(1);
        await Assert.That(events.Count(static e => e == "Release:b")).IsEqualTo(1);
        await Assert.That(events).Contains("Handled:b");
        await Assert.That(events[^1]).IsEqualTo("WakeConsumer");
        await Assert.That(queue.OutstandingLeases).IsEqualTo(0);
    }

    /// <summary>
    /// A release that throws still reports the worker idle, and the failure follows the existing
    /// worker error policy: the worker exits and completion faults with the error.
    /// </summary>
    /// <remarks>
    /// Disposal awaits the faulted completion, so it rethrows the error; the test disposes
    /// explicitly and asserts that instead of using <c>await using</c>.
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ReleaseThrows_StillReportsIdle_AndFaultsCompletion()
    {
        var log = new EventLog();
        var queue = new RecordingLeasedQueue(log) { ThrowOnRelease = true };
        var orchestrator = CreateWithQueue(new LoggingHandler(log), queue);
        var busyCount = 0;
        await Assert.That(orchestrator.Enqueue("a").IsAccepted).IsTrue();

        var run = orchestrator.CreateWorkerFunction(busy =>
        {
            Interlocked.Add(ref busyCount, busy ? 1 : -1);
            log.Add(busy ? "Busy" : "Idle");
        })("W", CancellationToken.None);
        await run.WaitAsync(WaitTimeout);

        await Assert.That(Volatile.Read(ref busyCount)).IsEqualTo(0);
        await Assert.That(orchestrator.ActiveWorkers).IsEqualTo(0);
        await Assert.That(Sequence(log.Snapshot())).IsEqualTo(Sequence(new[] { "Enqueue:a", "Dequeue:a", "Busy", "Handled:a", "Release:a", "Idle" }));
        await Assert.That(async () => await orchestrator.DrainAsync().WaitAsync(WaitTimeout))
            .Throws<ReleaseFaultException>();
        await Assert.That(async () => await orchestrator.DisposeAsync().AsTask().WaitAsync(WaitTimeout))
            .Throws<ReleaseFaultException>();
    }

    /// <summary>
    /// A release that throws still settles the item exactly once, after the release and before
    /// the idle report.
    /// </summary>
    /// <remarks>
    /// Receipts rely on this settle-once rule. Disposal rethrows the release fault, as in
    /// <see cref="ReleaseThrows_StillReportsIdle_AndFaultsCompletion"/>.
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ReleaseThrows_StillSettlesOnce_BetweenReleaseAndIdle()
    {
        var log = new EventLog();
        var queue = new RecordingLeasedQueue(log) { ThrowOnRelease = true };
        var orchestrator = CreateWithQueue(new LoggingHandler(log), queue);
        var settled = 0;
        orchestrator.Outcomes.Observed = record =>
        {
            Interlocked.Increment(ref settled);
            log.Add($"Settled:{record.Outcome}");
        };
        await Assert.That(orchestrator.Enqueue("a").IsAccepted).IsTrue();

        var run = orchestrator.CreateWorkerFunction(busy => log.Add(busy ? "Busy" : "Idle"))("W", CancellationToken.None);
        await run.WaitAsync(WaitTimeout);

        await Assert.That(Volatile.Read(ref settled)).IsEqualTo(1);
        await Assert.That(Sequence(log.Snapshot()))
            .IsEqualTo(Sequence(new[] { "Enqueue:a", "Dequeue:a", "Busy", "Handled:a", "Release:a", $"Settled:{TerminalOutcome.Succeeded}", "Idle" }));
        await Assert.That(async () => await orchestrator.DisposeAsync().AsTask().WaitAsync(WaitTimeout))
            .Throws<ReleaseFaultException>();
    }

    /// <summary>No built-in binding is leased, so the orchestrator never releases for them.</summary>
    /// <param name="strategy">The dispatch strategy under test.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    [Arguments(DispatchStrategy.Fifo)]
    [Arguments(DispatchStrategy.PriorityExact)]
    [Arguments(DispatchStrategy.PriorityRelaxed)]
    public async Task BuiltInBinding_IsNotLeased_AndDrainCompletes(DispatchStrategy strategy)
    {
        var log = new EventLog();
        await using var orchestrator = new WorkOrchestrator<string>(
            new LoggingHandler(log),
            Options.Create(new WorkOrchestratorOptions { WorkerCount = 1, DispatchStrategy = strategy, FullMode = QueueFullMode.Reject }),
            NullLogger<WorkOrchestrator<string>>.Instance);

        await Assert.That(orchestrator.WorkQueue is ILeasedWorkQueue<WorkEnvelope<string>>).IsFalse();
        await Assert.That(orchestrator.Enqueue("a").IsAccepted).IsTrue();
        await Assert.That(orchestrator.Enqueue("b").IsAccepted).IsTrue();
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);

        await Assert.That(log.Snapshot().Count(static e => e.StartsWith("Handled:", StringComparison.Ordinal))).IsEqualTo(2);
    }

    private static string Sequence(IEnumerable<string> events) => string.Join(" > ", events);

    private static WorkOrchestrator<string> CreateWithQueue(IWorkHandler<string> handler, RecordingLeasedQueue queue,
        TerminalOutcomeOptions<string>? outcomes = null)
        => new(
            handler,
            Options.Create(new WorkOrchestratorOptions { WorkerCount = 0, FullMode = QueueFullMode.Reject }),
            NullLogger<WorkOrchestrator<string>>.Instance,
            null,
            null,
            (_, _) => queue,
            outcomes ?? new TerminalOutcomeOptions<string>());

    private sealed class LoggingDeadLetterQueue(EventLog log, IDeadLetterQueue<string> inner) : IDeadLetterQueue<string>
    {
        public int Count => inner.Count;

        public ValueTask EnqueueAsync(DeadLetteredWork<string> item, CancellationToken ct = default)
        {
            log.Add($"Captured:{item.Work}");
            return inner.EnqueueAsync(item, ct);
        }

        public IAsyncEnumerable<DeadLetteredWork<string>> ReadAllAsync(CancellationToken ct = default) => inner.ReadAllAsync(ct);
    }

    private sealed class EventLog
    {
        private readonly Lock _gate = new();
        private readonly List<string> _events = [];
        private readonly List<(Func<List<string>, bool> Predicate, TaskCompletionSource Signal)> _waiters = [];

        public void Add(string entry)
        {
            lock (_gate)
            {
                _events.Add(entry);
                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (!_waiters[i].Predicate(_events)) continue;
                    _waiters[i].Signal.TrySetResult();
                    _waiters.RemoveAt(i);
                }
            }
        }

        public string[] Snapshot()
        {
            lock (_gate) return [.. _events];
        }

        public Task WaitForCountAsync(string entry, int count)
        {
            bool Predicate(List<string> events) => events.Count(e => e == entry) >= count;
            lock (_gate)
            {
                if (Predicate(_events)) return Task.CompletedTask;
                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((Predicate, signal));
                return signal.Task;
            }
        }
    }

    private sealed class LoggingHandler(EventLog log, Func<string, CancellationToken, ValueTask>? body = null) : IWorkHandler<string>
    {
        public async ValueTask HandleAsync(string work, CancellationToken ct)
        {
            if (body is not null) await body(work, ct);
            log.Add($"Handled:{work}");
        }
    }

    private sealed class ScopeProbe(EventLog log) : IAsyncDisposable
    {
        public EventLog Log => log;

        public ValueTask DisposeAsync()
        {
            log.Add("ScopeDisposed");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ProbeHandler(ScopeProbe probe) : IWorkHandler<string>
    {
        public ValueTask HandleAsync(string work, CancellationToken ct)
        {
            probe.Log.Add($"Handled:{work}");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ReleaseFaultException() : Exception("Release failed.");

    private sealed class RecordingLeasedQueue(EventLog log)
        : IWorkQueue<WorkEnvelope<string>>, IWorkQueueLifecycle, ILeasedWorkQueue<WorkEnvelope<string>>
    {
        private readonly Lock _gate = new();
        private readonly Queue<WorkEnvelope<string>> _items = new();
        private readonly Dictionary<object, string> _outstanding = new(ReferenceEqualityComparer.Instance);
        private readonly SemaphoreSlim _signal = new(0);
        private bool _completed;

        public bool ThrowOnRelease { get; init; }

        public int OutstandingLeases
        {
            get
            {
                lock (_gate) return _outstanding.Count;
            }
        }

        public int Count
        {
            get
            {
                lock (_gate) return _items.Count;
            }
        }

        public EnqueueResult Enqueue(in WorkEnvelope<string> item)
        {
            lock (_gate)
            {
                if (_completed) return EnqueueResult.Rejected(RejectionReason.Shutdown);
                _items.Enqueue(item);
                log.Add($"Enqueue:{item.Work}");
            }

            _signal.Release();
            return EnqueueResult.Accepted;
        }

        public bool TryEnqueue(in WorkEnvelope<string> item) => Enqueue(item).IsAccepted;

        public async ValueTask<bool> WaitToDequeueAsync(CancellationToken cancellationToken)
        {
            if (IsCompletedAndEmpty()) return false;
            await _signal.WaitAsync(cancellationToken);
            return !IsCompletedAndEmpty();
        }

        public bool TryDequeue([MaybeNullWhen(false)] out WorkEnvelope<string> item)
            => throw new InvalidOperationException("A leased binding must be dequeued through ILeasedWorkQueue.");

        public bool TryDequeue([MaybeNullWhen(false)] out WorkEnvelope<string> item, out object? lease)
        {
            lock (_gate)
            {
                if (!_items.TryDequeue(out item))
                {
                    lease = null;
                    return false;
                }

                lease = new object();
                _outstanding.Add(lease, item.Work);
                log.Add($"Dequeue:{item.Work}");
                return true;
            }
        }

        public void Release(object lease)
        {
            lock (_gate)
            {
                if (!_outstanding.Remove(lease, out var work))
                    throw new InvalidOperationException("Unknown or already released lease.");
                log.Add($"Release:{work}");
            }

            if (ThrowOnRelease) throw new ReleaseFaultException();
        }

        public int DiscardQueued(ICollection<WorkEnvelope<string>> discarded)
        {
            lock (_gate)
            {
                var removed = _items.Count;
                while (_items.TryDequeue(out var item))
                {
                    discarded.Add(item);
                    log.Add($"Discard:{item.Work}");
                }

                return removed;
            }
        }

        public void Complete()
        {
            lock (_gate)
            {
                if (_completed) return;
                _completed = true;
                log.Add("Complete");
            }

            _signal.Release(1024);
        }

        public void WakeConsumer()
        {
            log.Add("WakeConsumer");
            _signal.Release();
        }

        public void ConsumeMatchedWake()
        {
            lock (_gate)
            {
                log.Add("ConsumeMatchedWake");
                if (_completed && _items.Count == 0) return;
                _signal.Wait(0);
            }
        }

        private bool IsCompletedAndEmpty()
        {
            lock (_gate) return _completed && _items.Count == 0;
        }
    }
}
