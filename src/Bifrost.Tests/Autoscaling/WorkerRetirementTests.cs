using Bifrost.Autoscaling;
using Bifrost.Core;
using Bifrost.DependencyInjection;
using Bifrost.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bifrost.Tests.Autoscaling;

internal class WorkerRetirementTests
{
    [Test]
    [Arguments(DispatchStrategy.Fifo, false, false)]
    [Arguments(DispatchStrategy.PriorityExact, false, false)]
    [Arguments(DispatchStrategy.PriorityRelaxed, false, false)]
    [Arguments(DispatchStrategy.Fifo, true, false)]
    [Arguments(DispatchStrategy.PriorityExact, true, false)]
    [Arguments(DispatchStrategy.PriorityRelaxed, true, false)]
    [Arguments(DispatchStrategy.Fifo, false, true)]
    [Arguments(DispatchStrategy.PriorityExact, false, true)]
    [Arguments(DispatchStrategy.PriorityRelaxed, false, true)]
    public async Task ScaleDownFinishesCurrentWorkAndPreservesQueuedWork(
        DispatchStrategy strategy, bool busy, bool useDecorator)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var baseStarted = Signal();
        var releaseBase = Signal();
        var dynamicStarted = Signal();
        var releaseDynamic = Signal();
        var afterStop = Signal();
        CancellationToken handlerToken = default;
        int finished = 0;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkOrchestrator<int>(options =>
        {
            options.Capacity = 4;
            options.WorkerCount = 1;
            options.DispatchStrategy = strategy;
            options.FullMode = QueueFullMode.Reject;
        }).WithHandler(async (work, token) =>
        {
            if (work == 0)
            {
                baseStarted.TrySetResult();
                await releaseBase.Task.WaitAsync(token).ConfigureAwait(false);
            }
            else if (work == 1)
            {
                handlerToken = token;
                dynamicStarted.TrySetResult();
                await releaseDynamic.Task.WaitAsync(token).ConfigureAwait(false);
                Interlocked.Increment(ref finished);
            }
            else
            {
                afterStop.TrySetResult();
            }
        }).WithEventStream().WithDeadLetterQueue().WithResilience().WithAutoscaling(new AutoscalingOptions { MinWorkers = 1, MaxWorkers = 2 }).Build();
        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<int>>();
        var coordinator = provider.GetRequiredKeyedService<IAutoscalingCoordinator>(typeof(int));
        var registry = provider.GetRequiredKeyedService<IWorkerRegistry>(typeof(int));
        try
        {
            Require(orchestrator.TryEnqueue(0), "base admitted");
            await baseStarted.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            for (int cycle = 0; cycle < 3; cycle++)
            {
                dynamicStarted = Signal();
                releaseDynamic = Signal();
                if (useDecorator) await orchestrator.RequestScaleUpAsync(1, deadline.Token).ConfigureAwait(false);
                else await coordinator.RequestScaleUpAsync(1, deadline.Token).ConfigureAwait(false);
                Require(orchestrator.TryEnqueue(1), "dynamic admitted");
                await dynamicStarted.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                var worker = registry.GetAllWorkers().Single();
                if (!busy)
                {
                    releaseDynamic.TrySetResult();
                    await Until(() => worker.IsIdle, deadline.Token).ConfigureAwait(false);
                }

                if (useDecorator) await orchestrator.RequestScaleDownAsync(1, deadline.Token).ConfigureAwait(false);
                else await coordinator.RequestScaleDownAsync(1, deadline.Token).ConfigureAwait(false);
                Require(worker.StopRequested, "selected worker has a stop request");
                Require(!handlerToken.IsCancellationRequested, "soft stop must not cancel the handler");
                if (busy)
                {
                    if (cycle == 2) Require(orchestrator.TryEnqueue(2), "post-stop work admitted");
                    releaseDynamic.TrySetResult();
                }

                await Until(() => registry.ActiveWorkerCount == 0, deadline.Token).ConfigureAwait(false);
                Require(orchestrator.ActiveWorkers == 1, "only the blocked base worker remains");
                Require(finished == cycle + 1, "current handler completed normally");
                Require(!afterStop.Task.IsCompleted, "retired worker did not consume queued work");
            }

            if (!busy) Require(orchestrator.TryEnqueue(2), "idle retirement preserved admission");
            Require(orchestrator.PendingCount == 1, "queued work is conserved");
            releaseBase.TrySetResult();
            await orchestrator.DrainAsync(deadline.Token).ConfigureAwait(false);
            Require(afterStop.Task.IsCompleted && orchestrator.PendingCount == 0, "base worker drained queued work");
            Require(orchestrator.ActiveWorkers == 0, "drain retired all workers");
        }
        finally
        {
            releaseDynamic.TrySetResult();
            releaseBase.TrySetResult();
        }
    }

    [Test]
    [Arguments(DispatchStrategy.Fifo)]
    [Arguments(DispatchStrategy.PriorityExact)]
    [Arguments(DispatchStrategy.PriorityRelaxed)]
    public async Task CompletedScaleRequestDoesNotOwnWorkerLifetime(DispatchStrategy strategy)
    {
        using var request = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = Signal();
        var baseStarted = Signal();
        var canceled = Signal();
        CancellationToken handlerToken = default;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkOrchestrator<int>(options =>
        {
            options.Capacity = 4;
            options.WorkerCount = 1;
            options.DispatchStrategy = strategy;
            options.FullMode = QueueFullMode.Reject;
        }).WithHandler(async (work, token) =>
        {
            if (work == 0)
            {
                baseStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                return;
            }

            handlerToken = token;
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            }
            finally
            {
                canceled.TrySetResult();
            }
        }).WithEventStream().WithDeadLetterQueue().WithResilience()
            .WithAutoscaling(new AutoscalingOptions { MinWorkers = 1, MaxWorkers = 2 }).Build();
        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<int>>();
        var coordinator = provider.GetRequiredKeyedService<IAutoscalingCoordinator>(typeof(int));
        var registry = provider.GetRequiredKeyedService<IWorkerRegistry>(typeof(int));
        Require(orchestrator.TryEnqueue(0), "base work admitted");
        await baseStarted.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        await coordinator.RequestScaleUpAsync(1, request.Token).ConfigureAwait(false);
        Require(orchestrator.TryEnqueue(1), "dynamic work admitted");
        await entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        await request.CancelAsync().ConfigureAwait(false);
        Require(!handlerToken.IsCancellationRequested, "completed request does not cancel its worker");
        await coordinator.RequestScaleDownAsync(1, deadline.Token).ConfigureAwait(false);
        Require(!handlerToken.IsCancellationRequested, "busy retirement leaves handler running");
        await orchestrator.AbortAsync(deadline.Token).ConfigureAwait(false);
        await canceled.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        await Until(() => registry.ActiveWorkerCount == 0, deadline.Token).ConfigureAwait(false);
        Require(handlerToken.IsCancellationRequested && orchestrator.ActiveWorkers == 0,
            "host abort still cancels a retiring worker");
    }

    [Test]
    public async Task ConcurrentStopRequestsCountEachWorkerOnce()
    {
        var registry = new WorkerRegistry();
        var release = Signal();
        var worker = await registry.CreateWorkerAsync("busy", (_, token) => release.Task.WaitAsync(token), CancellationToken.None).ConfigureAwait(false);
        worker.MarkBusy();
        try
        {
            var counts = await Task.WhenAll(Enumerable.Range(0, 16)
                .Select(_ => Task.Run(() => registry.RequestMultipleWorkerStop(1)))).ConfigureAwait(false);
            Require(counts.Sum() == 1, "one worker receives one retirement request");
        }
        finally
        {
            release.TrySetResult();
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Until(() => registry.ActiveWorkerCount == 0, deadline.Token).ConfigureAwait(false);
        Require(!worker.RequestStop(), "retained metadata cannot cancel a disposed signal");
    }

    [Test]
    [Arguments(DispatchStrategy.PriorityExact)]
    [Arguments(DispatchStrategy.PriorityRelaxed)]
    public async Task RetirementHandsOffAnAcquiredPriorityWake(DispatchStrategy strategy)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var retirement = new CancellationTokenSource();
        var handled = Signal();
        await using var orchestrator = new WorkOrchestrator<int>(new SignalingHandler(handled),
            Options.Create(new WorkOrchestratorOptions
            {
                WorkerCount = 0, Capacity = 4, DispatchStrategy = strategy, FullMode = QueueFullMode.Reject,
            }), NullLogger<WorkOrchestrator<int>>.Instance);
        Require(orchestrator.TryEnqueue(42), "work admitted");
        // Model the boundary where a retiring consumer has acquired the only wake
        // but has not removed the envelope. No later enqueue or Complete may rescue it.
        Require(await orchestrator.WorkQueue.WaitToDequeueAsync(deadline.Token).ConfigureAwait(false), "wake acquired");
        await retirement.CancelAsync().ConfigureAwait(false);
        await orchestrator.CreateWorkerFunction(null, retirement.Token)("retiring", deadline.Token).ConfigureAwait(false);
        var survivor = orchestrator.CreateWorkerFunction()("survivor", deadline.Token);
        await handled.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        await orchestrator.DrainAsync(deadline.Token).ConfigureAwait(false);
        await survivor.ConfigureAwait(false);
        Require(orchestrator.PendingCount == 0, "survivor handled the retained envelope");
    }

    [Test]
    [Arguments(DispatchStrategy.Fifo)]
    [Arguments(DispatchStrategy.PriorityExact)]
    [Arguments(DispatchStrategy.PriorityRelaxed)]
    public async Task LastConsumerFinishesAdmittedWorkAfterRetirement(DispatchStrategy strategy)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var retirement = new CancellationTokenSource();
        var entered = Signal();
        var release = Signal();
        var finished = Signal();
        await using var orchestrator = new WorkOrchestrator<int>(new GatedHandler(entered, release, finished),
            Options.Create(new WorkOrchestratorOptions
            {
                WorkerCount = 0, Capacity = 4, DispatchStrategy = strategy, FullMode = QueueFullMode.Reject,
            }), NullLogger<WorkOrchestrator<int>>.Instance);
        var worker = orchestrator.CreateWorkerFunction(null, retirement.Token)("only", CancellationToken.None);
        try
        {
            Require(orchestrator.TryEnqueue(1), "current item admitted");
            await entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            Require(orchestrator.TryEnqueue(2), "queued item remains admitted");
            await retirement.CancelAsync().ConfigureAwait(false);
            release.TrySetResult();
            await finished.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            await orchestrator.DrainAsync(deadline.Token).ConfigureAwait(false);
            await worker.ConfigureAwait(false);
            Require(orchestrator.PendingCount == 0, "retirement drained the queued item");
        }
        finally
        {
            release.TrySetResult();
            await orchestrator.AbortAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed class GatedHandler(TaskCompletionSource entered, TaskCompletionSource release, TaskCompletionSource finished)
        : IWorkHandler<int>
    {
        public async ValueTask HandleAsync(int work, CancellationToken ct)
        {
            if (work == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct).ConfigureAwait(false);
                return;
            }

            finished.TrySetResult();
        }
    }

    private sealed class SignalingHandler(TaskCompletionSource handled) : IWorkHandler<int>
    {
        public ValueTask HandleAsync(int work, CancellationToken ct)
        {
            Require(work == 42, "retained identity");
            handled.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task Until(Func<bool> predicate, CancellationToken token)
    {
        while (!predicate()) await Task.Delay(1, token).ConfigureAwait(false);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
