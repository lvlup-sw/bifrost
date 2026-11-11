#pragma warning disable CS1591, SA1600

using System.Collections.Concurrent;

using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.DeadLetter;
using Bifrost.Resilience;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

namespace Bifrost.Tests.Systematic;

public sealed class LifecycleInvariantTests
{
    [Test]
    public async Task O1_O2_GatedLifecycleHistories()
    {
        for (var seed = 0; seed < 24; seed++)
        {
            var handler = new GatedHandler();
            var ledger = new AdmissionLedger();
            await using var actual = new WorkOrchestrator<string>(handler,
                Options.Create(new WorkOrchestratorOptions
                {
                    Capacity = 8,
                    WorkerCount = 1
                }),
                NullLogger<WorkOrchestrator<string>>.Instance);
            ledger.Accept("call-0", "work-0", actual.Enqueue("work-0").IsAccepted);
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var count = new Random(seed).Next(1, 8);
            for (var index = 1; index <= count; index++)
                ledger.Accept($"call-{index}", $"work-{index}", actual.Enqueue($"work-{index}").IsAccepted);
            Require(handler.Starts.Count == 1 && ledger.Accepted.Count == count + 1, "acceptance is distinct from execution");
            using var wait = new CancellationTokenSource();
            var draining = actual.DrainAsync(wait.Token);
            await wait.CancelAsync().ConfigureAwait(false);
            await ExpectCancellation(draining).ConfigureAwait(false);
            Require(!actual.GetShutdownToken().IsCancellationRequested && !handler.Canceled, "drain cancellation must not cancel workers");
            Require(!actual.Enqueue("late-work").IsAccepted, "completion closes admission");
            actual.Complete();
            if (seed % 3 == 0)
            {
                await actual.AbortAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Require(handler.Canceled && handler.Starts.Count == 1, "abort may abandon accepted pending work");
                Require(actual.PendingCount == count, "abandoned work remains pending");
            }

            else if (seed % 3 == 1)
            {
                await actual.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Require(handler.Canceled && handler.Starts.Count == 1, "dispose signals cancellation");
            }

            else
            {
                handler.Release.TrySetResult();
                await actual.Completion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Require(ledger.Accepted.SetEquals(handler.Successes), "graceful completion accounts for every accepted identity");
                Require(handler.Starts.Count == ledger.Accepted.Count, "one worker attempt per admitted identity");
            }

            Require(actual.ActiveWorkers == 0, "completion means worker exit");
        }
    }

    [Test]
    public async Task O1_AdmissionBoundariesAndCanceledProducer()
    {
        var handler = new GatedHandler();
        await using var actual = new WorkOrchestrator<string>(handler,
            Options.Create(new WorkOrchestratorOptions
            {
                Capacity = 1,
                WorkerCount = 0
            }),
            NullLogger<WorkOrchestrator<string>>.Instance);
        Require(actual.Enqueue("first").IsAccepted, "first accepted");
        Require(!actual.Enqueue("full").IsAccepted, "synchronous full rejected");
        using var cancel = new CancellationTokenSource();
        var waiting = actual.EnqueueAsync("waiting", ct: cancel.Token).AsTask();
        Require(!waiting.IsCompleted, "producer waits for capacity");
        await cancel.CancelAsync().ConfigureAwait(false);
        await ExpectCancellation(waiting).ConfigureAwait(false);
        actual.Complete();
        Require(!(await actual.EnqueueAsync("closed").ConfigureAwait(false)).IsAccepted, "closed asynchronous admission rejected");
        await actual.Completion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Require(actual.PendingCount == 1 && handler.Starts.IsEmpty, "no registered workers does not imply execution");
    }

    [Test]
    public async Task R1_AdmissionRetryBudgetsAndEffectIdentities()
    {
        for (var budget = 0; budget <= 3; budget++)
        {
            for (var failures = 0; failures <= budget + 1; failures++)
            {
                foreach (var postEffect in new[] { false, true })
                {
                    var inner = Substitute.For<IWorkOrchestrator<string>>();
                    var attempts = new List<string>();
                    var effects = new List<string>();
                    inner.EnqueueAsync(Arg.Any<string>(), Arg.Any<WorkEnqueueOptions>(), Arg.Any<CancellationToken>())
                        .Returns(_ => Attempt());
                    ValueTask<EnqueueResult> Attempt()
                    {
                        var attempt = $"call-0/attempt-{attempts.Count}";
                        attempts.Add(attempt);
                        if (postEffect || attempts.Count > failures) effects.Add(attempt + "/accepted-work-0");
                        return attempts.Count <= failures
                            ? ValueTask.FromException<EnqueueResult>(new TimeoutException("synthetic admission fault"))
                            : ValueTask.FromResult(EnqueueResult.Accepted);
                    }

                    var wrapper = Wrap(inner, budget);
                    var succeeded = false;
                    try
                    {
                        succeeded = (await wrapper.EnqueueAsync("work-0").ConfigureAwait(false)).IsAccepted;
                    }

                    catch (TimeoutException)
                    {
                    }

                    var expectedCalls = Math.Min(failures + 1, budget + 1);
                    Require(attempts.Count == expectedCalls, $"retry budget={budget} failures={failures}: {attempts.Count} != {expectedCalls}");
                    Require(succeeded == (failures <= budget), "final admission outcome");
                    Require(effects.Count == (postEffect ? expectedCalls : succeeded ? 1 : 0), "post-effect failure can cause repeated acceptance");
                    Require(attempts.Distinct().Count() == attempts.Count, "attempt identity is not work identity");
                }
            }
        }
    }

    [Test]
    public async Task R1_CancellationAndTimeoutCountRealInnerCalls()
    {
        foreach (var cancelCaller in new[] { true, false })
        {
            var calls = 0;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var inner = Substitute.For<IWorkOrchestrator<string>>();
            inner.EnqueueAsync(Arg.Any<string>(), Arg.Any<WorkEnqueueOptions>(), Arg.Any<CancellationToken>())
                .Returns(info => Block(info.Arg<CancellationToken>()));
            async ValueTask<EnqueueResult> Block(CancellationToken token)
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return EnqueueResult.Accepted;
            }

            using var cts = new CancellationTokenSource();
            var operation = Wrap(inner, 1).EnqueueAsync("work-0", ct: cts.Token).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (cancelCaller) await cts.CancelAsync().ConfigureAwait(false);
            Exception? failure = null;
            try
            {
                await operation.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            }

            catch (Exception ex)
            {
                failure = ex;
            }

            Require(cancelCaller ? failure is OperationCanceledException : failure is Polly.Timeout.TimeoutRejectedException, "cancellation/timeout outcome");
            Require(calls == (cancelCaller ? 1 : 2), "caller cancellation stops retries; timeout consumes attempt budget");
        }
    }

    [Test]
    public async Task D1_GeneratedSequentialDropOldestLedger()
    {
        for (var seed = 0; seed < 32; seed++)
        {
            var random = new Random(seed);
            var queue = NewQueue(NullLogger<DeadLetterQueue<string>>.Instance);
            string? retained = null;
            var written = 0;
            var read = 0;
            var dropped = 0;
            for (var step = 0; step < 48; step++)
            {
                if (random.Next(3) != 0)
                {
                    var id = $"seed-{seed}/write-{written++}";
                    if (retained is not null) dropped++;
                    retained = id;
                    await queue.EnqueueAsync(Item(id)).ConfigureAwait(false);
                }

                else
                {
                    await using var drain = queue.ReadAllAsync().GetAsyncEnumerator();
                    var moved = await drain.MoveNextAsync().ConfigureAwait(false);
                    Require(moved == (retained is not null), "retained identity exists");
                    if (moved)
                    {
                        Require(drain.Current.Work == retained, "DropOldest retains newest identity");
                        read++;
                        retained = null;
                    }
                }

                Require(queue.Count == (retained is null ? 0 : 1), "quiescent retained count");
                Require(queue.DroppedCount == dropped && written == read + queue.Count + queue.DroppedCount, "D1 conservation");
            }
        }
    }

    [Test]
    public async Task D1_ConcurrentGeneratedBatchesConserveIdentities()
    {
        for (var seed = 0; seed < 16; seed++)
        {
            var queue = NewQueue(NullLogger<DeadLetterQueue<string>>.Instance);
            var read = new ConcurrentBag<string>();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writers = Enumerable.Range(0, 4).Select(actor => Task.Run(async () =>
            {
                await start.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                for (var step = 0; step < 16; step++)
                {
                    await queue.EnqueueAsync(Item($"seed-{seed}/writer-{actor}/write-{step}")).ConfigureAwait(false);
                    if ((step + actor + seed) % 3 == 0) await Task.Yield();
                }
            })).ToArray();
            var readers = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            {
                await start.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                for (var step = 0; step < 16; step++)
                {
                    await foreach (var item in queue.ReadAllAsync().ConfigureAwait(false)) read.Add(item.Work);
                    await Task.Yield();
                }
            })).ToArray();
            start.TrySetResult();
            await Task.WhenAll(writers.Concat(readers)).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await foreach (var item in queue.ReadAllAsync().ConfigureAwait(false)) read.Add(item.Work);
            Require(read.Distinct().Count() == read.Count, $"seed={seed}: no identity read twice");
            Require(queue.Count == 0 && read.Count + queue.DroppedCount == 64,
                $"seed={seed}: written=64 read={read.Count} retained={queue.Count} dropped={queue.DroppedCount}; interleaving uncontrolled");
        }
    }

    [Test]
    public async Task D1_EarlyAndCanceledDrainPreservesResidual()
    {
        var queue = new DeadLetterQueue<string>(Options.Create(new DeadLetterQueueOptions
        {
            Capacity = 3
        }), NullLogger<DeadLetterQueue<string>>.Instance);
        foreach (var id in new[] { "a", "b", "c" }) await queue.EnqueueAsync(Item(id)).ConfigureAwait(false);
        await using (var drain = queue.ReadAllAsync().GetAsyncEnumerator())
        {
            Require(await drain.MoveNextAsync().ConfigureAwait(false) && drain.Current.Work == "a", "early drain first identity");
        }

        using var cts = new CancellationTokenSource();
        await using (var drain = queue.ReadAllAsync(cts.Token).GetAsyncEnumerator())
        {
            Require(await drain.MoveNextAsync().ConfigureAwait(false) && drain.Current.Work == "b", "second identity");
            await cts.CancelAsync().ConfigureAwait(false);
            await ExpectCancellation(Task.Run(async () => await drain.MoveNextAsync().ConfigureAwait(false))).ConfigureAwait(false);
        }

        Require(queue.Count == 1 && queue.DroppedCount == 0, "canceled enumeration retains c");
        var remaining = new List<string>();
        await foreach (var item in queue.ReadAllAsync().ConfigureAwait(false)) remaining.Add(item.Work);
        Require(remaining.SequenceEqual(new[]
{
"c"
}) && queue.Count == 0, "residual identity conservation");
    }

    [Test]
    public void D1_ConcurrentDropAndRead_RealReplayAndReduction()
    {
        var history = new HistoryCase(new()
        {
            ["capacity"] = 1
        },
            [new("write", "seed"), new("race-write-read", "next"), new("drain", "tail")]);
        var observed = EvaluateRace(history);
        if (observed.FailureClass.Length == 0) return;
        var reduced = HistoryReducer.Reduce(history, ValidRace, EvaluateRace);
        var record = ReplayCapture.Create("D1", "written = read + retained + dropped",
            reduced.History, null, reduced.Observation,
            "BIFROST_REPLAY_FILE=<path> dotnet run --project src/Bifrost.Tests -- --treenode-filter '/*/*/LifecycleInvariantTests/D1_SavedCounterexampleExecutesPersistedHistory'",
            ["seed-write", "writer-warning-enter", "reader-drain", "writer-release", "final-drain"],
            "Logger gate controls the overlap; callback placement may change which identity is read.", fault: 1);
        ReplayCapture.SaveFailure(record);
        var replay = ReplayRecord.FromJson(record.ToJson()).Replay(ValidRace, (source, _) => EvaluateRace(source));
        Require(replay == reduced.Observation, "real-component replay preserves failure class and observations");
        throw new InvalidOperationException(record.ToJson());
    }

    [Test]
    public void D1_SavedCounterexampleExecutesPersistedHistory()
    {
        var path = Environment.GetEnvironmentVariable("BIFROST_REPLAY_FILE") ?? D1Fixture();
        var record = ReplayRecord.FromJson(File.ReadAllText(path));
        Require(record.Invariant == "D1" && record.Followup is null, "D1 replay domain");
        var actual = record.Replay(ValidRace, (source, _) => EvaluateRace(source));
        if (Environment.GetEnvironmentVariable("BIFROST_REPLAY_EXPECT_FAILURE") == "1")
        {
            Require(actual == record.Observation, "persisted D1 failure reproduces exactly");
        }
        else
        {
            Require(actual.FailureClass.Length == 0, $"persisted D1 history failed: {actual}");
            Require(actual.Expected == record.Observation.Expected, "persisted D1 oracle relation is retained");
        }
    }

    private static string D1Fixture() =>
        Path.Combine(AppContext.BaseDirectory, "lifecycle-counterexample.json");

    private static bool ValidRace(HistoryCase history) => history.Configuration.TryGetValue("capacity", out var capacity) && capacity == 1
        && history.Commands.Select(x => x.Kind).SequenceEqual(new[]
{
"write", "race-write-read", "drain"
});

#pragma warning disable VSTHRD002
    private static HistoryObservation EvaluateRace(HistoryCase history)
    {
        var logger = new GatedLogger();
        var queue = NewQueue(logger);
        queue.EnqueueAsync(Item(history.Commands[0].Id)).GetAwaiter().GetResult();
        var writer = Task.Run(async () => await queue.EnqueueAsync(Item(history.Commands[1].Id)).ConfigureAwait(false));
        Require(logger.Entered.Wait(TimeSpan.FromSeconds(5)), "writer entered warning boundary");
        var read = new List<string>();
        try
        {
            Drain();
        }

        finally
        {
            logger.Release.Set();
        }

        writer.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        Drain();
        var sum = read.Count + queue.Count + queue.DroppedCount;
        return new(sum == 2 && queue.Count == 0 && read.Distinct().Count() == read.Count ? "" : "dlq-conservation",
            "written=2; retained=0; read+dropped=2", $"read={string.Join(',', read)}; retained={queue.Count}; dropped={queue.DroppedCount}; sum={sum}");
        void Drain()
        {
            var enumerator = queue.ReadAllAsync().GetAsyncEnumerator();
            try
            {
                while (enumerator.MoveNextAsync().GetAwaiter().GetResult()) read.Add(enumerator.Current.Work);
            }

            finally
            {
                enumerator.DisposeAsync().GetAwaiter().GetResult();
            }
        }
    }

#pragma warning restore VSTHRD002

    private static ResilientOrchestrator<string> Wrap(IWorkOrchestrator<string> inner, int budget) => new(inner,
        Options.Create(new ResiliencySettings
        {
            RetryCount = budget,
            RetryIntervalSeconds = 0,
            UseExponentialBackoff = false,
            TimeoutIntervalSeconds = 1
        }),
        NullLogger<ResilientOrchestrator<string>>.Instance);
    private static DeadLetterQueue<string> NewQueue(ILogger<DeadLetterQueue<string>> logger) => new(Options.Create(new DeadLetterQueueOptions
    {
        Capacity = 1
    }), logger);
    private static DeadLetteredWork<string> Item(string id) => new(id, null, 1, DateTimeOffset.UnixEpoch, id);
    private static void Require(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
    }

    private static async Task ExpectCancellation(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }

        catch (OperationCanceledException)
        {
            return;
        }

        throw new InvalidOperationException("Expected cancellation");
    }

    private sealed class AdmissionLedger
    {
        internal HashSet<string> Calls { get; } = [];

        internal HashSet<string> Accepted { get; } = [];

        internal void Accept(string call, string work, bool accepted)
        {
            Require(Calls.Add(call), "unique admission call");
            if (accepted) Require(Accepted.Add(work), "unique synthetic work identity");
        }
    }

    private sealed class GatedHandler : IWorkHandler<string>
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ConcurrentQueue<string> Starts { get; } = new();

        internal ConcurrentQueue<string> Successes { get; } = new();

        internal bool Canceled { get; private set; }

        public async ValueTask HandleAsync(string work, CancellationToken ct)
        {
            Starts.Enqueue(work);
            Entered.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(ct).ConfigureAwait(false);
                Successes.Enqueue(work);
            }
            catch (OperationCanceledException)
            {
                Canceled = true;
                throw;
            }
        }
    }

    private sealed class GatedLogger : ILogger<DeadLetterQueue<string>>
    {
        internal ManualResetEventSlim Entered { get; } = new();

        internal ManualResetEventSlim Release { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entered.Set();
            Require(Release.Wait(TimeSpan.FromSeconds(5)), "reader released writer");
        }
    }
}