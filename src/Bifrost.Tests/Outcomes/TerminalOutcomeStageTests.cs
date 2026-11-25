// =============================================================================
// <copyright file="TerminalOutcomeStageTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;

using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.Core.Events;
using Bifrost.DeadLetter;
using Bifrost.Outcomes;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TUnit.Core;

namespace Bifrost.Tests.Outcomes;

/// <summary>
/// Tests for the terminal outcome stage of <see cref="WorkOrchestrator{TWork}"/>: one outcome per
/// started item, attempts counted apart from the outcome, one capture per final failure, and the
/// capture-error, eviction, drop, and abandonment counts.
/// </summary>
/// <remarks>
/// Each orchestrator runs one worker. <see cref="IWorkOrchestrator{TWork}.DrainAsync"/> returns
/// after the last worker exits, and every item is settled before its worker reports idle, so the
/// counts are final when the drain returns. Timeouts are hang guards only.
/// </remarks>
[Property("Category", "Unit")]
public sealed class TerminalOutcomeStageTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A handler that returns normally settles as one succeeded attempt with no capture.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Success_SettlesSucceeded_WithOneAttempt()
    {
        var harness = Harness.Create(new DelegateHandler((_, _) => ValueTask.CompletedTask), deadLetterRetries: 3);
        await harness.RunAsync("a");

        await Assert.That(harness.Records).IsEquivalentTo(new[] { new TerminalOutcomeRecord(TerminalOutcome.Succeeded, 1, harness.Records[0].Duration, CaptureDisposition.None) });
        await Assert.That(harness.Counters.Succeeded).IsEqualTo(1);
        await Assert.That(harness.Counters.HandlerAttempts).IsEqualTo(1);
        await Assert.That(harness.Queue.Count).IsEqualTo(0);
    }

    /// <summary>Without a dead-letter queue a throwing handler gets one attempt and is not captured.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Throw_WithoutDeadLetterQueue_SettlesFailed_WithoutCapture()
    {
        var harness = Harness.Create(Throwing(), deadLetter: false);
        await harness.RunAsync("a");

        await Assert.That(harness.Records.Single().Outcome).IsEqualTo(TerminalOutcome.Failed);
        await Assert.That(harness.Records.Single().Attempts).IsEqualTo(1);
        await Assert.That(harness.Records.Single().Capture).IsEqualTo(CaptureDisposition.None);
        await Assert.That(harness.Counters.Failed).IsEqualTo(1);
        await Assert.That(harness.Counters.DeadLetterCaptured).IsEqualTo(0);
    }

    /// <summary>
    /// Dead-letter retries run until the attempts are used up, and the final failure is captured
    /// exactly once with the attempt count and the last exception.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DeadLetterRetries_ThenFinalFailure_CapturesOnce()
    {
        var calls = 0;
        var harness = Harness.Create(
            new DelegateHandler((_, _) =>
            {
                var call = Interlocked.Increment(ref calls);
                return ValueTask.FromException(new InvalidOperationException($"fail {call}"));
            }),
            deadLetterRetries: 2);
        var notified = new ConcurrentBag<WorkDeadLetteredEvent<string>>();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = harness.Notifier.Subscribe(evt =>
        {
            notified.Add(evt);
            delivered.TrySetResult();
        });

        await harness.RunAsync("a");
        await delivered.Task.WaitAsync(WaitTimeout);

        var entries = await harness.DrainDeadLettersAsync();
        await Assert.That(calls).IsEqualTo(3);
        await Assert.That(entries).HasCount(1);
        await Assert.That(entries[0].Work).IsEqualTo("a");
        await Assert.That(entries[0].AttemptCount).IsEqualTo(3);
        await Assert.That(entries[0].Exception!.Message).IsEqualTo("fail 3");
        await Assert.That(entries[0].FailedAt).IsEqualTo(harness.Time.GetUtcNow());
        await Assert.That(harness.Records.Single()).IsEqualTo(new TerminalOutcomeRecord(TerminalOutcome.Failed, 3, harness.Records[0].Duration, CaptureDisposition.Captured));
        await Assert.That(harness.Counters.Failed).IsEqualTo(1);
        await Assert.That(harness.Counters.HandlerAttempts).IsEqualTo(3);
        await Assert.That(harness.Counters.DeadLetterCaptured).IsEqualTo(1);
        await Assert.That(notified.Single().AttemptCount).IsEqualTo(3);
        await Assert.That(harness.Events.OfType<WorkDeadLetteredEvent<string>>().Single().AttemptCount).IsEqualTo(3);
    }

    /// <summary>An item that succeeds on a dead-letter retry is succeeded, its attempts counted, and nothing captured.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DeadLetterRetries_ThenSuccess_SettlesSucceeded_WithoutCapture()
    {
        var calls = 0;
        var harness = Harness.Create(
            new DelegateHandler((_, _) => Interlocked.Increment(ref calls) < 3
                ? ValueTask.FromException(new InvalidOperationException("transient"))
                : ValueTask.CompletedTask),
            deadLetterRetries: 3);

        await harness.RunAsync("a");

        await Assert.That(harness.Records.Single().Outcome).IsEqualTo(TerminalOutcome.Succeeded);
        await Assert.That(harness.Records.Single().Attempts).IsEqualTo(3);
        await Assert.That(harness.Records.Single().Capture).IsEqualTo(CaptureDisposition.None);
        await Assert.That(harness.Counters.Succeeded).IsEqualTo(1);
        await Assert.That(harness.Counters.Failed).IsEqualTo(0);
        await Assert.That(harness.Counters.HandlerAttempts).IsEqualTo(3);
        await Assert.That(harness.Queue.Count).IsEqualTo(0);
        await Assert.That(harness.Events.OfType<WorkCompletedEvent<string>>().Single().Success).IsTrue();
    }

    /// <summary>With zero dead-letter retries the first failure is captured at once.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ZeroRetries_CapturesFirstFailure()
    {
        var harness = Harness.Create(Throwing(), deadLetterRetries: 0);
        await harness.RunAsync("a");

        await Assert.That(harness.Records.Single().Attempts).IsEqualTo(1);
        await Assert.That(harness.Records.Single().Capture).IsEqualTo(CaptureDisposition.Captured);
        await Assert.That(harness.Queue.Count).IsEqualTo(1);
    }

    /// <summary>
    /// An <see cref="OperationCanceledException"/> the handler throws while its token is not
    /// cancelled is a failure: it is retried and captured.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task HandlerOwnCancellation_IsFailure_AndIsRetried()
    {
        var harness = Harness.Create(
            new DelegateHandler((_, _) => ValueTask.FromException(new OperationCanceledException("own timeout"))),
            deadLetterRetries: 1);
        await harness.RunAsync("a");

        await Assert.That(harness.Records.Single().Outcome).IsEqualTo(TerminalOutcome.Failed);
        await Assert.That(harness.Records.Single().Attempts).IsEqualTo(2);
        await Assert.That(harness.Counters.Canceled).IsEqualTo(0);
        await Assert.That(harness.Counters.DeadLetterCaptured).IsEqualTo(1);
    }

    /// <summary>
    /// Cancelling the handler token settles the running item as canceled after one attempt: it is
    /// not retried and not captured. The abort sweep removes the item still queued behind it and
    /// settles it as abandoned, with no completion event.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Abort_SettlesRunningItemCanceled_AndCountsQueuedItemAbandoned()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = Harness.Create(
            new DelegateHandler(async (_, ct) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }),
            deadLetterRetries: 3);
        await using (harness.Orchestrator)
        {
            await Assert.That(harness.Orchestrator.Enqueue("running").IsAccepted).IsTrue();
            await started.Task.WaitAsync(WaitTimeout);
            await Assert.That(harness.Orchestrator.Enqueue("queued").IsAccepted).IsTrue();

            await harness.Orchestrator.AbortAsync().WaitAsync(WaitTimeout);
        }

        var canceled = harness.Records.Single(r => r.Outcome == TerminalOutcome.Canceled);
        await Assert.That(canceled.Attempts).IsEqualTo(1);
        await Assert.That(canceled.Capture).IsEqualTo(CaptureDisposition.None);
        var abandoned = harness.Records.Single(r => r.Outcome == TerminalOutcome.Abandoned);
        await Assert.That(abandoned.Attempts).IsEqualTo(0);
        await Assert.That(harness.Records.Count).IsEqualTo(2);
        await Assert.That(harness.Counters.Canceled).IsEqualTo(1);
        await Assert.That(harness.Counters.Abandoned).IsEqualTo(1);
        await Assert.That(harness.Counters.HandlerAttempts).IsEqualTo(1);
        await Assert.That(harness.Queue.Count).IsEqualTo(0);
        await Assert.That(harness.Events.OfType<WorkCompletedEvent<string>>().Single().Success).IsFalse();
    }

    /// <summary>
    /// A cancellation on a later attempt is still a cancellation: after a retried failure, an
    /// <see cref="OperationCanceledException"/> thrown once the handler token is cancelled settles
    /// the item as canceled with both attempts counted, and nothing is captured or retried.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task CancelOnLaterAttempt_SettlesCanceled_WithoutFurtherRetry()
    {
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var harness = Harness.Create(
            new DelegateHandler(async (_, ct) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    throw new InvalidOperationException("transient");

                secondStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }),
            deadLetterRetries: 3);
        await using (harness.Orchestrator)
        {
            await Assert.That(harness.Orchestrator.Enqueue("a").IsAccepted).IsTrue();
            await secondStarted.Task.WaitAsync(WaitTimeout);

            await harness.Orchestrator.AbortAsync().WaitAsync(WaitTimeout);
        }

        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(2);
        await Assert.That(harness.Records.Single().Outcome).IsEqualTo(TerminalOutcome.Canceled);
        await Assert.That(harness.Records.Single().Attempts).IsEqualTo(2);
        await Assert.That(harness.Records.Single().Capture).IsEqualTo(CaptureDisposition.None);
        await Assert.That(harness.Counters.HandlerAttempts).IsEqualTo(2);
        await Assert.That(harness.Counters.DeadLetterCaptured).IsEqualTo(0);
        await Assert.That(harness.Queue.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Items still queued when the last worker exits are swept out of the queue and settled as
    /// abandoned once each, with zero attempts.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task CompleteWithoutWorkers_CountsQueuedItemsAbandoned()
    {
        var harness = Harness.Create(new DelegateHandler((_, _) => ValueTask.CompletedTask), workers: 0);
        await using (harness.Orchestrator)
        {
            await Assert.That(harness.Orchestrator.Enqueue("a").IsAccepted).IsTrue();
            await Assert.That(harness.Orchestrator.Enqueue("b").IsAccepted).IsTrue();
            await harness.Orchestrator.DrainAsync().WaitAsync(WaitTimeout);
            await harness.Orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        }

        await Assert.That(harness.Counters.Abandoned).IsEqualTo(2);
        await Assert.That(harness.Counters.HandlerAttempts).IsEqualTo(0);
        await Assert.That(harness.Records.Count).IsEqualTo(2);
        await Assert.That(harness.Records.All(r => r.Outcome == TerminalOutcome.Abandoned && r.Attempts == 0)).IsTrue();
        await Assert.That(harness.Orchestrator.PendingCount).IsEqualTo(0);
        await Assert.That(harness.Events).IsEmpty();
    }

    /// <summary>A drain that runs every item abandons nothing.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Drain_AfterAllItemsRun_AbandonsNothing()
    {
        var harness = Harness.Create(new DelegateHandler((_, _) => ValueTask.CompletedTask));
        await harness.RunAsync("a", "b", "c");

        await Assert.That(harness.Counters.Succeeded).IsEqualTo(3);
        await Assert.That(harness.Counters.Abandoned).IsEqualTo(0);
    }

    /// <summary>
    /// A dead-letter queue that throws is counted as a capture error. Subscribers hear nothing, and
    /// the worker goes on to the next item.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task CaptureError_IsCounted_AndWorkerContinues()
    {
        var harness = Harness.Create(
            new DelegateHandler((work, _) => work == "bad"
                ? ValueTask.FromException(new InvalidOperationException("boom"))
                : ValueTask.CompletedTask),
            deadLetterRetries: 0,
            queue: new ThrowingDeadLetterQueue());
        await harness.RunAsync("bad", "good");

        await Assert.That(harness.Records.Select(static r => r.Capture)).IsEquivalentTo(new[] { CaptureDisposition.CaptureFailed, CaptureDisposition.None });
        await Assert.That(harness.Counters.DeadLetterCaptureErrors).IsEqualTo(1);
        await Assert.That(harness.Counters.DeadLetterCaptured).IsEqualTo(0);
        await Assert.That(harness.Counters.Failed).IsEqualTo(1);
        await Assert.That(harness.Counters.Succeeded).IsEqualTo(1);
        await Assert.That(harness.Events.OfType<WorkDeadLetteredEvent<string>>()).IsEmpty();
    }

    /// <summary>A full built-in dead-letter queue counts each dropped entry as an eviction.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task FullDeadLetterQueue_CountsEvictions_ApartFromCaptures()
    {
        var harness = Harness.Create(Throwing(), deadLetterRetries: 0, deadLetterCapacity: 1);
        await harness.RunAsync("a", "b", "c");

        await Assert.That(harness.Counters.DeadLetterCaptured).IsEqualTo(3);
        await Assert.That(harness.Counters.DeadLetterEvictions).IsEqualTo(2);
        await Assert.That(harness.Queue.Count).IsEqualTo(1);
    }

    /// <summary>A notification the notifier drops because too many callbacks are running is counted.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Notifier_CountsDroppedNotifications()
    {
        var counters = new TerminalOutcomeCounters<string>();
        using var notifier = new DeadLetterNotifier<string>(NullLogger<DeadLetterNotifier<string>>.Instance, counters);
        using var gate = new SemaphoreSlim(0);
        using var subscription = notifier.SubscribeAsync(_ => gate.WaitAsync());

        for (var i = 0; i <= DeadLetterNotifier<string>.MaxOutstandingCallbacks; i++)
            notifier.Notify(new WorkDeadLetteredEvent<string>("a", null, 1, DateTimeOffset.UnixEpoch));

        await Assert.That(counters.DeadLetterNotificationsDropped).IsEqualTo(1);
        notifier.Dispose();
        gate.Release(DeadLetterNotifier<string>.MaxOutstandingCallbacks);
    }

    /// <summary>
    /// The lease is released before the outcome is settled, and the outcome is settled before the
    /// worker reports idle.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Settlement_RunsAfterHandlerExit_AndBeforeIdle()
    {
        var log = new ConcurrentQueue<string>();
        var harness = Harness.Create(
            new DelegateHandler((work, _) =>
            {
                log.Enqueue($"Handled:{work}");
                return ValueTask.CompletedTask;
            }),
            workers: 0);
        harness.Orchestrator.Outcomes.Observed += record => log.Enqueue($"Settled:{record.Outcome}");
        await Assert.That(harness.Orchestrator.Enqueue("a").IsAccepted).IsTrue();

        var run = harness.Orchestrator.CreateWorkerFunction(busy => log.Enqueue(busy ? "Busy" : "Idle"))("W", CancellationToken.None);
        await harness.Orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        await run.WaitAsync(WaitTimeout);
        await harness.Orchestrator.DisposeAsync();

        await Assert.That(string.Join(",", log)).IsEqualTo("Busy,Handled:a,Settled:Succeeded,Idle");
    }

    /// <summary>A throwing observer or event publisher does not stop the worker.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ThrowingObserverAndPublisher_DoNotStopTheWorker()
    {
        var handled = 0;
        var harness = Harness.Create(
            new DelegateHandler((_, _) =>
            {
                Interlocked.Increment(ref handled);
                return ValueTask.CompletedTask;
            }),
            publisher: _ => throw new InvalidOperationException("publisher"));
        harness.Orchestrator.Outcomes.Observed = _ => throw new InvalidOperationException("observer");

        await harness.RunAsync("a", "b");

        await Assert.That(handled).IsEqualTo(2);
        await Assert.That(harness.Counters.Succeeded).IsEqualTo(2);
    }

    /// <summary>Each started item publishes exactly one completion event with its final result.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task CompletionEvents_OnePerItem_WithFinalResult()
    {
        var harness = Harness.Create(
            new DelegateHandler((work, _) => work == "bad"
                ? ValueTask.FromException(new InvalidOperationException("boom"))
                : ValueTask.CompletedTask),
            deadLetterRetries: 2);
        await harness.RunAsync("good", "bad");

        var completed = harness.Events.OfType<WorkCompletedEvent<string>>().Select(static e => $"{e.Work}:{e.Success}");
        await Assert.That(string.Join(",", completed)).IsEqualTo("good:True,bad:False");
    }

    /// <summary>A negative dead-letter retry count is rejected when the stage is created.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Stage_RejectsNegativeRetries()
    {
        await Assert.That(() => new TerminalOutcomeStage<string>(
                new TerminalOutcomeOptions<string> { DeadLetterRetries = -1 },
                NullLogger.Instance,
                TimeProvider.System))
            .Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>The public constructor gives one attempt per item and no capture.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task PublicConstructor_UsesOneAttempt()
    {
        await using var orchestrator = new WorkOrchestrator<string>(
            Throwing(),
            Options.Create(new WorkOrchestratorOptions { WorkerCount = 1 }),
            NullLogger<WorkOrchestrator<string>>.Instance);
        await orchestrator.EnqueueAsync("a");
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);

        await Assert.That(orchestrator.Outcomes.MaxAttempts).IsEqualTo(1);
        await Assert.That(orchestrator.Outcomes.Counters.Failed).IsEqualTo(1);
        await Assert.That(orchestrator.Outcomes.Counters.HandlerAttempts).IsEqualTo(1);
    }

    private static DelegateHandler Throwing()
        => new((_, _) => ValueTask.FromException(new InvalidOperationException("always fails")));

    private sealed class Harness
    {
        private readonly ConcurrentQueue<TerminalOutcomeRecord> _records = new();
        private readonly ConcurrentQueue<IOrchestratorEvent> _events = new();

        private Harness()
        {
        }

        internal WorkOrchestrator<string> Orchestrator { get; private set; } = null!;

        internal TerminalOutcomeCounters<string> Counters { get; } = new();

        internal IDeadLetterQueue<string> Queue { get; private set; } = null!;

        internal DeadLetterNotifier<string> Notifier { get; private set; } = null!;

        internal FixedTimeProvider Time { get; } = new();

        internal IReadOnlyList<TerminalOutcomeRecord> Records => [.. _records];

        internal IReadOnlyList<IOrchestratorEvent> Events => [.. _events];

        internal static Harness Create(
            IWorkHandler<string> handler,
            bool deadLetter = true,
            int deadLetterRetries = 3,
            int deadLetterCapacity = 100,
            IDeadLetterQueue<string>? queue = null,
            int workers = 1,
            Action<IOrchestratorEvent>? publisher = null)
        {
            var harness = new Harness();
            harness.Queue = queue ?? new DeadLetterQueue<string>(
                Options.Create(new DeadLetterQueueOptions { Capacity = deadLetterCapacity }),
                NullLogger<DeadLetterQueue<string>>.Instance,
                harness.Counters);
            harness.Notifier = new DeadLetterNotifier<string>(NullLogger<DeadLetterNotifier<string>>.Instance, harness.Counters);
            var outcomes = new TerminalOutcomeOptions<string>
            {
                DeadLetterQueue = deadLetter ? harness.Queue : null,
                DeadLetterRetries = deadLetterRetries,
                Notifier = deadLetter ? harness.Notifier : null,
                EventPublisher = publisher ?? harness._events.Enqueue,
                Counters = harness.Counters,
            };
            harness.Orchestrator = new WorkOrchestrator<string>(
                handler,
                Options.Create(new WorkOrchestratorOptions { WorkerCount = workers }),
                NullLogger<WorkOrchestrator<string>>.Instance,
                harness.Time,
                null,
                null,
                outcomes);
            harness.Orchestrator.Outcomes.Observed = harness._records.Enqueue;
            return harness;
        }

        internal async Task RunAsync(params string[] items)
        {
            await using (Orchestrator)
            {
                foreach (var item in items)
                    await Assert.That(Orchestrator.Enqueue(item).IsAccepted).IsTrue();

                await Orchestrator.DrainAsync().WaitAsync(WaitTimeout);
            }
        }

        internal async Task<List<DeadLetteredWork<string>>> DrainDeadLettersAsync()
        {
            var entries = new List<DeadLetteredWork<string>>();
            await foreach (var entry in Queue.ReadAllAsync())
                entries.Add(entry);

            return entries;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class DelegateHandler(Func<string, CancellationToken, ValueTask> body) : IWorkHandler<string>
    {
        public ValueTask HandleAsync(string work, CancellationToken ct) => body(work, ct);
    }

    private sealed class ThrowingDeadLetterQueue : IDeadLetterQueue<string>
    {
        public int Count => 0;

        public ValueTask EnqueueAsync(DeadLetteredWork<string> item, CancellationToken ct = default)
            => ValueTask.FromException(new InvalidOperationException("store down"));

        public IAsyncEnumerable<DeadLetteredWork<string>> ReadAllAsync(CancellationToken ct = default)
            => AsyncEnumerable.Empty<DeadLetteredWork<string>>();
    }
}
