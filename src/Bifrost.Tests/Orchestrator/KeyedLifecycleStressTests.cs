// =============================================================================
// <copyright file="KeyedLifecycleStressTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;

using Bifrost.Core;
using Bifrost.Outcomes;
using Bifrost.Queues;
using Bifrost.Tests.Systematic;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bifrost.Tests.Orchestrator;

/// <summary>
/// Seeded stress campaign for keyed dispatch across its whole lifecycle: concurrent admission,
/// slot release, dynamic worker start and retirement, and a final drain or abort, over many keys
/// with one slot per key and with several.
/// </summary>
/// <remarks>
/// <para>
/// The repository has no controlled scheduler for the core library, so the campaign uses real
/// threads. Each run takes its shape from a fixed seed: the key count, the producer, base-worker,
/// and item counts, the limits, the handler behavior of each item, the scaler's actions, and
/// whether the run ends in a drain after admission, a drain during admission, or an abort.
/// Interleavings still vary between runs, so the campaign checks invariants rather than one exact
/// history. Every wait is bounded, and a run that hangs fails with a <see cref="TimeoutException"/>
/// whose message names its seed and cap. The run's deadline token flows into admission, the
/// ending, and the producers, so a hung run is cancelled rather than left running, and cleanup
/// always stops the scaler, retires the dynamic workers, and disposes the orchestrator within a
/// bounded time. The ending waits for admission progress on a signal the producers complete, not
/// by polling. Base workers never retire, so the last-consumer
/// retirement drain is covered by the targeted lifecycle tests, not by this campaign. <c>BIFROST_INVARIANT_SEED_MULTIPLIER</c> runs
/// more seeds, as it does for the other invariant campaigns.
/// </para>
/// <para>
/// Each producer owns its own keys, so the order of its accepted admissions for a key is that
/// key's admission order. The invariants checked after each run are:
/// </para>
/// <list type="bullet">
/// <item><description>No key ever runs more items than its concurrency limit.</description></item>
/// <item><description>With one slot per key, each key's items start in admission order.</description></item>
/// <item><description>Each key's dequeued items are a prefix of its admitted items, and its swept items are the rest.</description></item>
/// <item><description>
/// Every accepted item gets exactly one terminal outcome: each one is either dequeued and started
/// once, or swept once, never both, and the outcome counts match.
/// </description></item>
/// <item><description>Every lease is released exactly once, and no release is rejected.</description></item>
/// <item><description>A drain completes with nothing abandoned. Abort completes too.</description></item>
/// <item><description>No slot leaks: at the end every key is removed and the queue is empty.</description></item>
/// <item><description>Every sampled backlog snapshot has non-negative ready and blocked counts.</description></item>
/// </list>
/// </remarks>
public sealed class KeyedLifecycleStressTests
{
    private const int SeedsPerConfiguration = 12;
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OuterTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Runs the campaign for one per-key concurrency limit over the fixed seeds.</summary>
    /// <param name="maxConcurrencyPerKey">The per-key concurrency limit.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    [Arguments(1)]
    [Arguments(3)]
    public async Task SeededCampaign_HoldsLifecycleInvariants(int maxConcurrencyPerKey)
    {
        var seeds = InvariantRun.SeedCount(SeedsPerConfiguration);
        for (var seed = 0; seed < seeds; seed++)
        {
            var run = new StressRun(seed, maxConcurrencyPerKey);
            await ExecuteWithSeedAsync(run, seed, maxConcurrencyPerKey);
            var failures = run.Verify();
            await Assert.That(failures).IsEmpty().Because($"seed {seed}, cap {maxConcurrencyPerKey}: {string.Join("; ", failures)}");
        }
    }

    /// <summary>Runs one seeded run under a deadline that cancels the run itself.</summary>
    /// <remarks>
    /// The deadline token cannot interrupt a thread blocked on a lock, such as the orchestrator's
    /// lifecycle gate inside <c>DrainAsync</c> or <c>AbortAsync</c>. So the run, cleanup included,
    /// runs on its own task, and the wait for it is bounded by <see cref="OuterTimeout"/>. If that
    /// bound fires, the test fails with the seed and cap; the stuck task cannot be stopped from
    /// inside the process and is left behind.
    /// </remarks>
    /// <param name="run">The run.</param>
    /// <param name="seed">The run's seed, for the failure message.</param>
    /// <param name="cap">The per-key concurrency limit, for the failure message.</param>
    /// <returns>A task representing the run.</returns>
    /// <exception cref="TimeoutException">
    /// Thrown with the seed and cap when the run misses its deadline or its cleanup does not finish.
    /// </exception>
    private static async Task ExecuteWithSeedAsync(StressRun run, int seed, int cap)
    {
        using var deadline = new CancellationTokenSource(RunTimeout);
        var execution = Task.Run(() => run.ExecuteAsync(deadline.Token));
        try
        {
            await execution.WaitAsync(OuterTimeout);
        }
        catch (TimeoutException error) when (!execution.IsCompleted)
        {
            throw new TimeoutException($"seed {seed}, cap {cap}: the run and its cleanup did not finish within {OuterTimeout.TotalSeconds:F0}s, so a lock or a handler is stuck.", error);
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"seed {seed}, cap {cap}: the run did not finish within {RunTimeout.TotalSeconds:F0}s.", error);
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException($"seed {seed}, cap {cap}: {error.Message}", error);
        }
    }

    /// <summary>How a run ends: drain after admission, drain during admission, or abort.</summary>
    private static class Ending
    {
        public const int DrainAfterAdmission = 0;
        public const int DrainDuringAdmission = 1;
        public const int Abort = 2;
    }

    private static string KeyOf(string work) => work[..work.IndexOf(':', StringComparison.Ordinal)];

    /// <summary>A stable hash of an item and a seed, so each item's handler behavior is fixed by the seed.</summary>
    private static uint Hash(string work, int seed)
    {
        var hash = 2166136261u ^ (uint)seed;
        foreach (var c in work) hash = (hash ^ c) * 16777619u;
        return hash;
    }

    /// <summary>One seeded run: its shape, its orchestrator, and everything it records.</summary>
    private sealed class StressRun
    {
        private readonly int _seed;
        private readonly int _cap;
        private readonly Random _random;
        private readonly int _keyCount;
        private readonly int _producerCount;
        private readonly int _baseWorkers;
        private readonly int _itemsPerProducer;
        private readonly int _ending;
        private readonly ObservedKeyedQueue _queue;
        private readonly WorkOrchestrator<string> _orchestrator;
        private readonly StressHandler _handler;
        private readonly ConcurrentQueue<TerminalOutcomeRecord> _records = new();
        private readonly ConcurrentQueue<string> _violations = new();
        private readonly List<string>[] _admittedByProducer;
        private readonly ConcurrentQueue<Task> _dynamicWorkers = new();
        private readonly ConcurrentQueue<CancellationTokenSource> _retirements = new();
        private readonly TaskCompletionSource _admissionProgress = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _endAfter;
        private int _accepted;
        private int _producersDone;

        public StressRun(int seed, int cap)
        {
            _seed = seed;
            _cap = cap;
            _random = new Random(seed);
            _keyCount = _random.Next(4, 48);
            _producerCount = _random.Next(2, 5);
            _baseWorkers = _random.Next(1, 4);
            _itemsPerProducer = _random.Next(60, 160);
            _ending = seed % 3;
            var capacity = _random.Next(24, 96);
            var maxPendingPerKey = _random.Next(2, 10);
            _admittedByProducer = [.. Enumerable.Range(0, _producerCount).Select(_ => new List<string>())];
            _handler = new StressHandler(seed, cap, _violations);
            _queue = new ObservedKeyedQueue(KeyedWorkQueue.Create<string, string>(KeyOf, capacity, maxPendingPerKey, cap));
            _orchestrator = new WorkOrchestrator<string>(
                _handler,
                Options.Create(new WorkOrchestratorOptions
                {
                    WorkerCount = _baseWorkers,
                    Capacity = capacity,
                    FullMode = QueueFullMode.Reject,
                    DispatchStrategy = DispatchStrategy.Keyed,
                }),
                NullLogger<WorkOrchestrator<string>>.Instance,
                null,
                null,
                (_, _) => _queue,
                new TerminalOutcomeOptions<string>());
            _orchestrator.Outcomes.Observed = _records.Enqueue;
            _endAfter = _random.Next(0, _producerCount * _itemsPerProducer);
            ReportAdmissionProgress();
        }

        /// <summary>Runs admission, the scaler, and the seeded ending, then stops everything.</summary>
        /// <param name="cancellationToken">The run's deadline; it cancels admission, the ending, and the producers.</param>
        /// <returns>A task representing the run.</returns>
        /// <remarks>
        /// The ending starts once the admission-progress signal completes: enough items are accepted, or
        /// every producer is done. <see cref="StopAsync"/> runs whether or not the run finished.
        /// </remarks>
        public async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            using var stopScaler = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var producers = Enumerable.Range(0, _producerCount)
                .Select(p => Task.Run(() => ProduceAsync(p, _seed * 31 + p, cancellationToken)))
                .ToArray();
            var scaler = Task.Run(() => ScaleAsync(_seed * 17 + 5, stopScaler.Token));
            var finished = false;
            try
            {
                await _admissionProgress.Task.WaitAsync(cancellationToken);
                switch (_ending)
                {
                    case Ending.Abort:
                        await _orchestrator.AbortAsync(cancellationToken);
                        break;
                    case Ending.DrainDuringAdmission:
                        await _orchestrator.DrainAsync(cancellationToken);
                        break;
                    default:
                        await Task.WhenAll(producers).WaitAsync(cancellationToken);
                        await _orchestrator.DrainAsync(cancellationToken);
                        break;
                }

                await Task.WhenAll(producers).WaitAsync(cancellationToken);
                finished = true;
            }
            finally
            {
                await StopAsync(producers, scaler, stopScaler, finished);
            }
        }

        public List<string> Verify()
        {
            var failures = new List<string>(_violations);
            var admitted = _admittedByProducer.SelectMany(a => a).ToList();
            var dequeued = _queue.Dequeued;
            var discarded = _queue.Discarded;
            var started = _handler.StartCounts();
            Check(failures, admitted.Count == Volatile.Read(ref _accepted), "admission ledger matches the accepted count");
            Check(failures, admitted.Distinct().Count() == admitted.Count, "admitted items are distinct");
            Check(failures, dequeued.Distinct().Count() == dequeued.Length, "no item is dequeued twice");
            Check(failures, discarded.Distinct().Count() == discarded.Length, "no item is swept twice");
            Check(failures, !dequeued.Intersect(discarded).Any(), "no item is both dequeued and swept");
            Check(failures, dequeued.Concat(discarded).ToHashSet().SetEquals(admitted), "every accepted item is dequeued or swept");
            Check(failures, started.Keys.ToHashSet().SetEquals(dequeued), "every dequeued item started, and only those");
            Check(failures, started.Values.All(count => count == 1), "every started item started once");
            Check(failures, _queue.Releases == dequeued.Length, $"one release per lease ({_queue.Releases} releases, {dequeued.Length} dequeues)");
            Check(failures, _queue.FailedReleases == 0, "no release is rejected");

            var counters = _orchestrator.Outcomes.Counters;
            var settled = counters.Succeeded + counters.Failed + counters.Canceled;
            Check(failures, _records.Count == admitted.Count, $"one outcome per accepted item ({_records.Count} outcomes, {admitted.Count} accepted)");
            Check(failures, counters.Abandoned == discarded.Length, "abandoned outcomes match swept items");
            Check(failures, settled == dequeued.Length, "started outcomes match dequeued items");
            Check(failures, counters.HandlerAttempts == dequeued.Length, "one attempt per started item");
            if (_ending != Ending.Abort) Check(failures, discarded.Length == 0, "a drain abandons nothing");

            Check(failures, _orchestrator.Completion.IsCompletedSuccessfully, "completion succeeded");
            Check(failures, _orchestrator.ActiveWorkers == 0, "every worker exited");
            Check(failures, _queue.Inner.TrackedKeyCountForTest == 0, $"every key is removed ({_queue.Inner.TrackedKeyCountForTest} left)");
            Check(failures, _queue.Inner.ReadyKeyCountForTest == 0, "the ready list is empty");
            Check(failures, _queue.Count == 0 && _queue.DispatchableCount == 0, "the queue is empty");

            foreach (var producer in _admittedByProducer)
            {
                foreach (var key in producer.GroupBy(KeyOf))
                {
                    var order = key.ToList();
                    var taken = dequeued.Where(w => KeyOf(w) == key.Key).ToHashSet();
                    var prefix = order.Take(taken.Count).ToHashSet();
                    Check(failures, prefix.SetEquals(taken), $"key {key.Key}: dequeued items are an admission prefix");
                    Check(failures, order.Skip(taken.Count).ToHashSet().SetEquals(discarded.Where(w => KeyOf(w) == key.Key)),
                        $"key {key.Key}: swept items are the admission suffix");
                    if (_cap == 1)
                    {
                        var starts = _handler.StartsOf(key.Key);
                        Check(failures, starts.SequenceEqual(order.Take(starts.Length)), $"key {key.Key}: cap-1 items start in admission order");
                    }
                }
            }

            return failures;
        }

        private static void Check(List<string> failures, bool condition, string invariant)
        {
            if (!condition) failures.Add(invariant);
        }

        /// <summary>
        /// Stops the scaler and releases everything the run started, each step, including every
        /// cancellation, bounded by <see cref="CleanupTimeout"/>.
        /// </summary>
        /// <param name="producers">The producer tasks.</param>
        /// <param name="scaler">The scaler task.</param>
        /// <param name="stopScaler">The source that stops the scaler.</param>
        /// <param name="finished">Whether the run's ending completed.</param>
        /// <returns>A task representing the cleanup.</returns>
        /// <remarks>
        /// After a finished run, the scaler and every dynamic worker must exit on their own and the
        /// orchestrator must dispose in time, or cleanup throws a <see cref="TimeoutException"/>: a
        /// worker that does not exit after the drain or abort is a failure. Otherwise, or when one of
        /// those steps fails, cleanup forces the stop: it waits for the scaler first, so that it starts
        /// no worker after the retirements are cancelled, then retires every dynamic worker, disposes
        /// the orchestrator, and waits for the producers and workers. Each forced step is bounded and
        /// suppresses its exception, because the run already failed and the first failure is the one
        /// reported. The retirement sources are disposed last in every case.
        /// </remarks>
        private async Task StopAsync(Task[] producers, Task scaler, CancellationTokenSource stopScaler, bool finished)
        {
            var stopped = false;
            try
            {
                await stopScaler.CancelAsync().WaitAsync(CleanupTimeout);
                if (finished)
                {
                    await scaler.WaitAsync(CleanupTimeout);
                    await Task.WhenAll(_dynamicWorkers).WaitAsync(CleanupTimeout);
                    await _orchestrator.DisposeAsync().AsTask().WaitAsync(CleanupTimeout);
                    stopped = true;
                }
            }
            finally
            {
                if (!stopped)
                {
                    await scaler.WaitAsync(CleanupTimeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    foreach (var retirement in _retirements) await retirement.CancelAsync().WaitAsync(CleanupTimeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    await _orchestrator.DisposeAsync().AsTask().WaitAsync(CleanupTimeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    await Task.WhenAll([.. producers, .. _dynamicWorkers]).WaitAsync(CleanupTimeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }

                foreach (var retirement in _retirements) retirement.Dispose();
            }
        }

        /// <summary>Completes the admission-progress signal once enough items are accepted or every producer is done.</summary>
        private void ReportAdmissionProgress()
        {
            if (Volatile.Read(ref _accepted) >= _endAfter || Volatile.Read(ref _producersDone) >= _producerCount)
                _admissionProgress.TrySetResult();
        }

        private async Task ProduceAsync(int producer, int seed, CancellationToken cancellationToken)
        {
            var random = new Random(seed);
            var ownKeys = Enumerable.Range(0, _keyCount).Where(k => k % _producerCount == producer).ToArray();
            if (ownKeys.Length == 0) ownKeys = [producer];
            var ledger = _admittedByProducer[producer];
            try
            {
                for (var n = 0; n < _itemsPerProducer && !cancellationToken.IsCancellationRequested; n++)
                {
                    var work = $"k{ownKeys[random.Next(ownKeys.Length)]}:{producer}:{n}";
                    var result = _orchestrator.Enqueue(work);
                    if (result.IsAccepted)
                    {
                        ledger.Add(work);
                        Interlocked.Increment(ref _accepted);
                        ReportAdmissionProgress();
                    }
                    else if (result.Reason == RejectionReason.Shutdown)
                    {
                        return;
                    }
                    else if (result.Reason is not (RejectionReason.CapacityExceeded or RejectionReason.KeyLimitExceeded))
                    {
                        _violations.Enqueue($"unexpected rejection {result.Reason}");
                    }

                    switch (random.Next(8))
                    {
                        case 0:
                            await Task.Yield();
                            break;
                        case 1:
                            await Task.Delay(1, cancellationToken);
                            break;
                        default:
                            Thread.SpinWait(random.Next(20, 200));
                            break;
                    }
                }
            }
            finally
            {
                Interlocked.Increment(ref _producersDone);
                ReportAdmissionProgress();
            }
        }

        private async Task ScaleAsync(int seed, CancellationToken stop)
        {
            var random = new Random(seed);
            var live = new List<CancellationTokenSource>();
            var next = 0;
            while (!stop.IsCancellationRequested)
            {
                switch (random.Next(4))
                {
                    case 0 when live.Count < 4:
                    {
                        var retirement = new CancellationTokenSource();
                        _retirements.Enqueue(retirement);
                        live.Add(retirement);
                        var id = $"dynamic-{next++}";
                        var function = _orchestrator.CreateWorkerFunction(null, retirement.Token);
                        _dynamicWorkers.Enqueue(Task.Run(() => function(id, CancellationToken.None)));
                        break;
                    }

                    case 1 when live.Count > 0:
                    {
                        var index = random.Next(live.Count);
                        await live[index].CancelAsync();
                        live.RemoveAt(index);
                        break;
                    }

                    default:
                    {
                        var backlog = _orchestrator.Backlog;
                        if (backlog.Ready < 0 || backlog.Blocked < 0 || backlog.Pending < 0)
                            _violations.Enqueue($"invalid backlog snapshot {backlog}");
                        break;
                    }
                }

                try
                {
                    await Task.Delay(random.Next(0, 3), stop);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Records per-key concurrency and start order, and runs a behavior fixed by the item and seed:
    /// return at once, yield, spin, delay with or without honoring cancellation, or throw.
    /// </summary>
    private sealed class StressHandler(int seed, int cap, ConcurrentQueue<string> violations) : IWorkHandler<string>
    {
        private readonly ConcurrentDictionary<string, int> _running = new();
        private readonly ConcurrentDictionary<string, int> _startCounts = new();
        private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _startsByKey = new();

        public Dictionary<string, int> StartCounts() => new(_startCounts);

        public string[] StartsOf(string key) => _startsByKey.TryGetValue(key, out var starts) ? [.. starts] : [];

        public async ValueTask HandleAsync(string work, CancellationToken ct)
        {
            var key = KeyOf(work);
            var running = _running.AddOrUpdate(key, 1, static (_, n) => n + 1);
            if (running > cap) violations.Enqueue($"key {key} ran {running} items at once (cap {cap})");
            _startCounts.AddOrUpdate(work, 1, static (_, n) => n + 1);
            _startsByKey.GetOrAdd(key, static _ => new ConcurrentQueue<string>()).Enqueue(work);
            try
            {
                switch (Hash(work, seed) % 10)
                {
                    case 0:
                        throw new InvalidOperationException("Synthetic handler failure.");
                    case 1:
                    case 2:
                        await Task.Yield();
                        break;
                    case 3:
                    case 4:
                        await Task.Delay(1, ct);
                        break;
                    case 5:
                        await Task.Delay(1, CancellationToken.None);
                        break;
                    case 6:
                        Thread.SpinWait(500);
                        break;
                    default:
                        break;
                }
            }
            finally
            {
                _running.AddOrUpdate(key, 0, static (_, n) => n - 1);
            }
        }
    }
}
