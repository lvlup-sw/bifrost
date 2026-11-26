// =============================================================================
// <copyright file="AutomaticMetricsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.DependencyInjection;
using Bifrost.OpenTelemetry;
using Bifrost.Outcomes;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using TUnit.Core;

namespace Bifrost.Tests.OpenTelemetry;

/// <summary>
/// Verifies the metrics that <c>WithOpenTelemetry</c> records without handler code: processing
/// counts and duration from terminal outcomes, outcome, attempt, and dead-letter counters,
/// rejection counts by reason, and the pending, ready, and blocked gauges. Also verifies that no
/// work key ever appears as a metric tag under keyed load.
/// </summary>
[Property("Category", "Integration")]
public sealed class AutomaticMetricsTests
{
    private const string Processed = "orchestrator.items.processed";
    private const string Failed = "orchestrator.items.failed";
    private const string Duration = "orchestrator.processing.duration";
    private const string Pending = "orchestrator.queue.pending";
    private const string Ready = "bifrost.orchestrator.queue.ready";
    private const string Blocked = "bifrost.orchestrator.queue.blocked";
    private const string Outcomes = "bifrost.orchestrator.outcomes";
    private const string Attempts = "bifrost.orchestrator.handler.attempts";
    private const string Rejected = "bifrost.orchestrator.rejected";
    private const string Captured = "bifrost.orchestrator.deadletter.captured";
    private const string CaptureErrors = "bifrost.orchestrator.deadletter.capture_errors";
    private const string Evictions = "bifrost.orchestrator.deadletter.evictions";
    private const string NotificationsDropped = "bifrost.orchestrator.deadletter.notifications_dropped";

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly HashSet<string> AllowedTagKeys = ["work.class", "rejection.reason", "outcome"];

    /// <summary>The new instruments have stable names and units.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    [Property("Category", "Unit")]
    public async Task NewInstruments_HaveStableNamesAndUnits()
    {
        using var metrics = new OrchestratorMetrics<string>(() => null);

        await Assert.That(metrics.ReadyItems.Name).IsEqualTo(Ready);
        await Assert.That(metrics.ReadyItems.Unit).IsEqualTo("{item}");
        await Assert.That(metrics.BlockedItems.Name).IsEqualTo(Blocked);
        await Assert.That(metrics.BlockedItems.Unit).IsEqualTo("{item}");
        await Assert.That(metrics.Outcomes.Name).IsEqualTo(Outcomes);
        await Assert.That(metrics.Outcomes.Unit).IsEqualTo("{item}");
        await Assert.That(metrics.HandlerAttempts.Name).IsEqualTo(Attempts);
        await Assert.That(metrics.HandlerAttempts.Unit).IsEqualTo("{attempt}");
        await Assert.That(metrics.DeadLetterCaptured.Name).IsEqualTo(Captured);
        await Assert.That(metrics.DeadLetterCaptureErrors.Name).IsEqualTo(CaptureErrors);
        await Assert.That(metrics.DeadLetterEvictions.Name).IsEqualTo(Evictions);
        await Assert.That(metrics.DeadLetterNotificationsDropped.Name).IsEqualTo(NotificationsDropped);
        await Assert.That(metrics.DeadLetterNotificationsDropped.Unit).IsEqualTo("{notification}");
    }

    /// <summary>
    /// An instance that was never attached reports every pending item as ready, no blocked item,
    /// and no outcome, attempt, or dead-letter counts.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    [Property("Category", "Unit")]
    public async Task Unattached_ReportsPendingAsReady_AndNoOutcomeCounts()
    {
        var orchestrator = Substitute.For<IWorkOrchestrator<string>>();
        orchestrator.PendingCount.Returns(5);
        using var metrics = new OrchestratorMetrics<string>(() => orchestrator);
        using var recorder = new MeasurementRecorder(metrics.ItemsProcessed.Meter);

        recorder.Collect();

        await Assert.That(recorder.Last(Pending)).IsEqualTo(5d);
        await Assert.That(recorder.Last(Ready)).IsEqualTo(5d);
        await Assert.That(recorder.Last(Blocked)).IsEqualTo(0d);
        await Assert.That(recorder.Of(Outcomes)).IsEmpty();
        await Assert.That(recorder.Of(Attempts)).IsEmpty();
        await Assert.That(recorder.Of(Captured)).IsEmpty();
        await Assert.That(recorder.Of(Evictions)).IsEmpty();
    }

    /// <summary>
    /// An attached orchestrator without keyed dispatch reports every pending item as ready and no
    /// blocked item.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AttachedFifo_ReportsPendingAsReady_AndNoBlocked()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = Host<string>.Create(
            new DelegateHandler<string>(async (_, ct) =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(ct);
            }),
            static _ => { });

        await Assert.That(host.Orchestrator.Enqueue("running").IsAccepted).IsTrue();
        await started.Task.WaitAsync(WaitTimeout);
        await Assert.That(host.Orchestrator.Enqueue("a").IsAccepted).IsTrue();
        await Assert.That(host.Orchestrator.Enqueue("b").IsAccepted).IsTrue();
        host.Recorder.Collect();

        await Assert.That(host.Recorder.Last(Pending)).IsEqualTo(2d);
        await Assert.That(host.Recorder.Last(Ready)).IsEqualTo(2d);
        await Assert.That(host.Recorder.Last(Blocked)).IsEqualTo(0d);
        release.TrySetResult();
        await host.Orchestrator.DrainAsync().WaitAsync(WaitTimeout);
    }

    /// <summary>
    /// Processed, failed, duration, outcome, attempt, and capture metrics are recorded from terminal
    /// outcomes with no handler code: one record per item after its last attempt, attempts counted
    /// apart.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WithOpenTelemetry_RecordsProcessingFromTerminalOutcomes()
    {
        var calls = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        await using var host = Host<string>.Create(
            new DelegateHandler<string>((work, _) =>
            {
                var call = calls.AddOrUpdate(work, 1, static (_, n) => n + 1);
                return work switch
                {
                    "bad" => ValueTask.FromException(new InvalidOperationException("always")),
                    "flaky" when call == 1 => ValueTask.FromException(new InvalidOperationException("once")),
                    _ => ValueTask.CompletedTask,
                };
            }),
            static b => b.WithDeadLetterQueue(static o => o.MaxRetries = 2));

        foreach (var work in new[] { "ok", "bad", "flaky" })
            await Assert.That(host.Orchestrator.Enqueue(work).IsAccepted).IsTrue();
        await host.Orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        host.Recorder.Collect();

        await Assert.That(host.Recorder.Sum(Processed)).IsEqualTo(2d);
        await Assert.That(host.Recorder.Sum(Failed)).IsEqualTo(1d);
        await Assert.That(host.Recorder.Of(Duration)).HasCount(3);
        await Assert.That(host.Recorder.Of(Duration).Count(m => m.Tag("outcome") == "Succeeded")).IsEqualTo(2);
        await Assert.That(host.Recorder.Of(Duration).Count(m => m.Tag("outcome") == "Failed")).IsEqualTo(1);
        await Assert.That(host.Recorder.Last(Outcomes, "outcome", "Succeeded")).IsEqualTo(2d);
        await Assert.That(host.Recorder.Last(Outcomes, "outcome", "Failed")).IsEqualTo(1d);
        await Assert.That(host.Recorder.Last(Outcomes, "outcome", "Canceled")).IsEqualTo(0d);
        await Assert.That(host.Recorder.Last(Outcomes, "outcome", "Abandoned")).IsEqualTo(0d);
        await Assert.That(host.Recorder.Last(Attempts)).IsEqualTo(6d);
        await Assert.That(host.Recorder.Last(Captured)).IsEqualTo(1d);
        await Assert.That(host.Recorder.Last(CaptureErrors)).IsEqualTo(0d);
    }

    /// <summary>
    /// Abort during a running handler records the running item as canceled (duration only, not
    /// processed or failed) and the queued item as abandoned (no duration).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Abort_RecordsCanceledDurationOnly_AndAbandonedOutcome()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = Host<string>.Create(
            new DelegateHandler<string>(async (_, ct) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }),
            static _ => { });

        await Assert.That(host.Orchestrator.Enqueue("running").IsAccepted).IsTrue();
        await started.Task.WaitAsync(WaitTimeout);
        await Assert.That(host.Orchestrator.Enqueue("queued").IsAccepted).IsTrue();
        await host.Orchestrator.AbortAsync().WaitAsync(WaitTimeout);
        host.Recorder.Collect();

        await Assert.That(host.Recorder.Sum(Processed)).IsEqualTo(0d);
        await Assert.That(host.Recorder.Sum(Failed)).IsEqualTo(0d);
        await Assert.That(host.Recorder.Of(Duration)).HasCount(1);
        await Assert.That(host.Recorder.Of(Duration)[0].Tag("outcome")).IsEqualTo("Canceled");
        await Assert.That(host.Recorder.Last(Outcomes, "outcome", "Canceled")).IsEqualTo(1d);
        await Assert.That(host.Recorder.Last(Outcomes, "outcome", "Abandoned")).IsEqualTo(1d);
        await Assert.That(host.Recorder.Last(Attempts)).IsEqualTo(1d);
    }

    /// <summary>
    /// The dead-letter counters report captures, evictions from a full built-in queue, and dropped
    /// notifications from the shared counters.
    /// </summary>
    /// <remarks>
    /// The dropped-notification count is driven directly on the shared counters, so this checks
    /// only that the observable counter reads them. A real notifier drop is covered at stage level
    /// by <c>TerminalOutcomeStageTests.Notifier_CountsDroppedNotifications</c>.
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DeadLetterCounters_ReportCapturesEvictionsAndDrops()
    {
        await using var host = Host<string>.Create(
            new DelegateHandler<string>(static (_, _) => ValueTask.FromException(new InvalidOperationException("fail"))),
            static b => b.WithDeadLetterQueue(static o =>
            {
                o.MaxRetries = 0;
                o.Capacity = 1;
            }));

        foreach (var work in new[] { "a", "b", "c" })
            await Assert.That(host.Orchestrator.Enqueue(work).IsAccepted).IsTrue();
        await host.Orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        host.Provider.GetRequiredService<TerminalOutcomeCounters<string>>().RecordNotificationDropped();
        host.Recorder.Collect();

        await Assert.That(host.Recorder.Last(Captured)).IsEqualTo(3d);
        await Assert.That(host.Recorder.Last(Evictions)).IsEqualTo(2d);
        await Assert.That(host.Recorder.Last(NotificationsDropped)).IsEqualTo(1d);
        await Assert.That(host.Recorder.Last(CaptureErrors)).IsEqualTo(0d);
        await Assert.That(host.Recorder.Sum(Failed)).IsEqualTo(3d);
    }

    /// <summary>A dead-letter queue that throws is reported as a capture error, not a capture.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ThrowingDeadLetterQueue_ReportsCaptureError()
    {
        var deadLetters = Substitute.For<IDeadLetterQueue<string>>();
        deadLetters.EnqueueAsync(Arg.Any<DeadLetteredWork<string>>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException(new InvalidOperationException("store down")));
        await using var host = Host<string>.Create(
            new DelegateHandler<string>(static (_, _) => ValueTask.FromException(new InvalidOperationException("fail"))),
            static b => b.WithDeadLetterQueue(static o => o.MaxRetries = 0),
            services => services.AddSingleton(deadLetters));

        await Assert.That(host.Orchestrator.Enqueue("a").IsAccepted).IsTrue();
        await host.Orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        host.Recorder.Collect();

        await Assert.That(host.Recorder.Last(CaptureErrors)).IsEqualTo(1d);
        await Assert.That(host.Recorder.Last(Captured)).IsEqualTo(0d);
        await Assert.That(host.Recorder.Last(Outcomes, "outcome", "Failed")).IsEqualTo(1d);
    }

    /// <summary>
    /// Under keyed load with many distinct keys, failures, retries, captures, and both admission
    /// limits, the gauges report aggregate pending, ready, and blocked counts, rejections are counted
    /// by reason including <c>KeyLimitExceeded</c>, and no tag of any instrument carries a work key.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// Two workers run the first items of keys 0 and 1, each key at its concurrency limit of one.
    /// Two more items of key 0 are blocked and the first item of key 2 is ready. Every tag of every
    /// recorded measurement is then checked against the full list of keys, and every tag key against
    /// the fixed set <c>work.class</c>, <c>rejection.reason</c>, and <c>outcome</c>.
    /// </remarks>
    [Test]
    public async Task KeyedLoad_ReportsAggregateBacklog_AndNoTagCarriesAWorkKey()
    {
        var keys = Enumerable.Range(0, 5).Select(static i => $"tenant-{i}-{Guid.NewGuid():N}").ToArray();
        using var started = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        await using var host = Host<KeyedTurn>.Create(
            new DelegateHandler<KeyedTurn>(async (turn, ct) =>
            {
                started.Release();
                await release.WaitAsync(ct);
                if (turn.Fail) throw new InvalidOperationException($"turn failed for {turn.Key}");
            }),
            static b => b
                .WithKeyedDispatch(static t => t.Key, maxPendingPerKey: 2, maxConcurrencyPerKey: 1)
                .WithDeadLetterQueue(static o => o.MaxRetries = 1)
                .WithEventStream(),
            configure: static o =>
            {
                o.WorkerCount = 2;
                o.Capacity = 6;
            });
        var orchestrator = host.Orchestrator;

        await Assert.That(orchestrator.Enqueue(new KeyedTurn(keys[0], 1)).IsAccepted).IsTrue();
        await Assert.That(orchestrator.Enqueue(new KeyedTurn(keys[1], 1, Fail: true)).IsAccepted).IsTrue();
        await started.WaitAsync(WaitTimeout);
        await started.WaitAsync(WaitTimeout);
        await Assert.That(orchestrator.Enqueue(new KeyedTurn(keys[0], 2)).IsAccepted).IsTrue();
        await Assert.That(orchestrator.Enqueue(new KeyedTurn(keys[0], 3)).IsAccepted).IsTrue();
        await Assert.That(orchestrator.Enqueue(new KeyedTurn(keys[0], 4)).Reason).IsEqualTo(RejectionReason.KeyLimitExceeded);
        await Assert.That(orchestrator.Enqueue(new KeyedTurn(keys[2], 1)).IsAccepted).IsTrue();

        host.Recorder.Collect();
        await Assert.That(host.Recorder.Last(Pending)).IsEqualTo(3d);
        await Assert.That(host.Recorder.Last(Ready)).IsEqualTo(1d);
        await Assert.That(host.Recorder.Last(Blocked)).IsEqualTo(2d);

        await Assert.That(orchestrator.Enqueue(new KeyedTurn(keys[3], 1)).IsAccepted).IsTrue();
        await Assert.That(orchestrator.Enqueue(new KeyedTurn(keys[3], 2, Fail: true)).IsAccepted).IsTrue();
        await Assert.That(orchestrator.Enqueue(new KeyedTurn(keys[4], 1)).IsAccepted).IsTrue();
        await Assert.That(orchestrator.Enqueue(new KeyedTurn(keys[4], 2)).Reason).IsEqualTo(RejectionReason.CapacityExceeded);

        release.Release(100);
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        host.Recorder.Collect();
        var measurements = host.Recorder.Snapshot();

        await Assert.That(host.Recorder.Sum(Rejected, "rejection.reason", nameof(RejectionReason.KeyLimitExceeded))).IsEqualTo(1d);
        await Assert.That(host.Recorder.Sum(Rejected, "rejection.reason", nameof(RejectionReason.CapacityExceeded))).IsEqualTo(1d);
        await Assert.That(host.Recorder.Last(Outcomes, "outcome", "Succeeded")).IsEqualTo(6d);
        await Assert.That(host.Recorder.Last(Outcomes, "outcome", "Failed")).IsEqualTo(2d);
        await Assert.That(host.Recorder.Last(Attempts)).IsEqualTo(10d);
        await Assert.That(host.Recorder.Last(Captured)).IsEqualTo(2d);
        await Assert.That(host.Recorder.Last(Pending)).IsEqualTo(0d);
        await Assert.That(host.Recorder.Last(Ready)).IsEqualTo(0d);
        await Assert.That(host.Recorder.Last(Blocked)).IsEqualTo(0d);

        var instruments = measurements.Select(static m => m.Instrument).ToHashSet(StringComparer.Ordinal);
        foreach (var expected in new[] { Processed, Failed, Duration, Pending, Ready, Blocked, Outcomes, Attempts, Rejected, Captured, CaptureErrors, Evictions, NotificationsDropped })
            await Assert.That(instruments).Contains(expected);

        var leaks = measurements
            .SelectMany(static m => m.Tags.Select(t => (m.Instrument, t.Key, t.Value)))
            .Where(t => !AllowedTagKeys.Contains(t.Key) || keys.Any(k => (t.Value ?? string.Empty).Contains(k, StringComparison.Ordinal) || t.Key.Contains(k, StringComparison.Ordinal)))
            .Select(static t => $"{t.Instrument}:{t.Key}={t.Value}")
            .ToList();
        await Assert.That(leaks).IsEmpty();
    }

    /// <summary>A keyed work item.</summary>
    /// <param name="Key">The work key.</param>
    /// <param name="Sequence">The item's position within its key.</param>
    /// <param name="Fail">Whether every handler attempt for the item throws.</param>
    internal sealed record KeyedTurn(string Key, int Sequence, bool Fail = false);

    private sealed class DelegateHandler<TWork>(Func<TWork, CancellationToken, ValueTask> handle) : IWorkHandler<TWork>
    {
        public ValueTask HandleAsync(TWork work, CancellationToken ct) => handle(work, ct);
    }

    /// <summary>A built orchestrator with <c>WithOpenTelemetry</c> and a recorder on its meter.</summary>
    /// <typeparam name="TWork">The work item type.</typeparam>
    private sealed class Host<TWork> : IAsyncDisposable
    {
        private Host(ServiceProvider provider)
        {
            Provider = provider;
            Orchestrator = provider.GetRequiredService<IWorkOrchestrator<TWork>>();
            Recorder = new MeasurementRecorder(provider.GetRequiredService<OrchestratorMetrics<TWork>>().ItemsProcessed.Meter);
        }

        public ServiceProvider Provider { get; }

        public IWorkOrchestrator<TWork> Orchestrator { get; }

        public MeasurementRecorder Recorder { get; }

        public static Host<TWork> Create(
            IWorkHandler<TWork> handler,
            Action<WorkOrchestratorBuilder<TWork>> compose,
            Action<IServiceCollection>? services = null,
            Action<WorkOrchestratorOptions>? configure = null)
        {
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddSingleton(handler);
            services?.Invoke(collection);
            var builder = collection.AddWorkOrchestrator<TWork>(configure ?? (static o => o.WorkerCount = 1));
            compose(builder);
            builder.WithOpenTelemetry().Build();
            return new Host<TWork>(collection.BuildServiceProvider());
        }

        public async ValueTask DisposeAsync()
        {
            await Orchestrator.DisposeAsync();
            Recorder.Dispose();
            await Provider.DisposeAsync();
        }
    }
}
