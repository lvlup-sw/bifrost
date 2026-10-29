// =============================================================================
// <copyright file="ApiRemediationTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling;
using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.DependencyInjection;
using Bifrost.Hosting;
using Bifrost.Queues;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

namespace Bifrost.Tests;

/// <summary>Regression tests for API, admission, isolation and lifecycle corrections.</summary>
public class ApiRemediationTests
{
    /// <summary>Exact and relaxed selection are explicit; waiting priority admission is invalid.</summary>
    [Test]
    public async Task OrderingAndFullModeAreIndependentAndValidated()
    {
        foreach (var ordering in Enum.GetValues<DispatchStrategy>())
        {
            await using var queue = Create(ordering);
            await Assert.That(queue.Ordering).IsEqualTo(ordering);
            await Assert.That(queue.Enqueue("first").IsAccepted).IsTrue();
            await Assert.That(queue.WorkQueue.GetType().Name).IsEqualTo(ordering switch
            {
                DispatchStrategy.Fifo => "FifoChannelWorkQueue`1",
                DispatchStrategy.PriorityExact => "LockingPriorityWorkQueue`1",
                _ => "ConcurrentPriorityWorkQueue`1",
            });
        }

        await Assert.That(() => new WorkOrchestrator<string>(Substitute.For<IWorkHandler<string>>(),
            Options.Create(new WorkOrchestratorOptions { DispatchStrategy = DispatchStrategy.PriorityExact }),
            NullLogger<WorkOrchestrator<string>>.Instance)).Throws<ArgumentException>();
        await Assert.That(default(EnqueueResult).Status).IsEqualTo(EnqueueStatus.Uninitialized);
    }

    /// <summary>Omitted classification and explicitly Default classification differ.</summary>
    [Test]
    public async Task ExplicitDefaultBypassesClassifier()
    {
        await using var queue = Create(DispatchStrategy.Fifo, classifier: _ => WorkClass.Batch);
        queue.Enqueue("implicit");
        queue.Enqueue("explicit", new WorkEnqueueOptions(WorkClass.Default));
        queue.TryReadEnvelope(out var implicitItem);
        queue.TryReadEnvelope(out var explicitItem);
        await Assert.That(implicitItem.Class).IsEqualTo(WorkClass.Batch);
        await Assert.That(explicitItem.Class).IsEqualTo(WorkClass.Default);
    }

    /// <summary>Each binding reports the actual admission decision.</summary>
    /// <param name="ordering">The priority strategy.</param>
    [Test]
    [Arguments(DispatchStrategy.PriorityExact)]
    [Arguments(DispatchStrategy.PriorityRelaxed)]
    public async Task WatermarkCapacityAndCompletionHaveDistinctOutcomes(DispatchStrategy ordering)
    {
        await using var queue = Create(ordering);
        for (int i = 0; i < 9; i++) queue.Enqueue(i.ToString(), WorkClass.Interactive);
        await Assert.That(queue.Enqueue("batch", WorkClass.Batch).Reason).IsEqualTo(RejectionReason.WatermarkExceeded);
        await Assert.That(queue.Enqueue("urgent", WorkClass.Interactive).IsAccepted).IsTrue();
        await Assert.That((await queue.EnqueueAsync("full", WorkClass.Interactive).ConfigureAwait(false)).Reason).IsEqualTo(RejectionReason.CapacityExceeded);
        queue.Complete();
        await Assert.That(queue.Enqueue("closed").Reason).IsEqualTo(RejectionReason.Shutdown);
    }

    /// <summary>A dynamically registered worker is included in completion and safe disposal.</summary>
    [Test]
    public async Task DrainWaitsForDynamicHandlerAndCanceledWaitDoesNotAbortIt()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = Substitute.For<IWorkHandler<string>>();
        handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            started.TrySetResult();
            return new ValueTask(release.Task);
        });
        await using var queue = Create(DispatchStrategy.PriorityExact, handler);
        queue.Enqueue("work");
        Task worker = queue.CreateWorkerFunction()("dynamic", CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync().ConfigureAwait(false);
        Task? disposal = null;
        try
        {
            await Assert.That(async () => await queue.DrainAsync(canceled.Token).ConfigureAwait(false)).Throws<TaskCanceledException>();
            await Assert.That(queue.Completion.IsCompleted).IsFalse();
            await Assert.That(queue.GetShutdownToken().IsCancellationRequested).IsFalse();
            disposal = queue.DisposeAsync().AsTask();
            await Assert.That(disposal.IsCompleted).IsFalse();
            await Assert.That(((LockingPriorityWorkQueue<string>)queue.WorkQueue).IsDisposedForTest).IsFalse();
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(worker, disposal ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        await Assert.That(queue.Completion.IsCompletedSuccessfully).IsTrue();
    }

    /// <summary>Different work types retain separate options and autoscaling components.</summary>
    [Test]
    public async Task RegistrationIsIsolatedByWorkType()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddWorkOrchestrator<string>(o => o.Capacity = 13)
            .WithHandler((string _, CancellationToken _) => ValueTask.CompletedTask)
            .UsePriorityDispatch(DispatchStrategy.PriorityExact)
            .WithDeadLetterQueue(o => o.Capacity = 17).WithAutoscaling().Build();
        services.AddWorkOrchestrator<int>(o => o.Capacity = 23)
            .WithHandler((int _, CancellationToken _) => ValueTask.CompletedTask)
            .WithDeadLetterQueue(o => o.Capacity = 29).WithAutoscaling().Build();
        await using var provider = services.BuildServiceProvider();
        var first = provider.GetRequiredService<IWorkOrchestrator<string>>();
        var second = provider.GetRequiredService<IWorkOrchestrator<int>>();
        await Assert.That(first.Capacity).IsEqualTo(13);
        await Assert.That(second.Capacity).IsEqualTo(23);
        await Assert.That(provider.GetRequiredService<IWorkWriter<string>>()).IsSameReferenceAs(first);
        var settings = provider.GetRequiredService<IOptionsMonitor<DeadLetterQueueOptions>>();
        await Assert.That(settings.Get(WorkOptions.Name<string>()).Capacity).IsEqualTo(17);
        await Assert.That(settings.Get(WorkOptions.Name<int>()).Capacity).IsEqualTo(29);
        await Assert.That(provider.GetRequiredKeyedService<IWorkerRegistry>(typeof(string)))
            .IsNotSameReferenceAs(provider.GetRequiredKeyedService<IWorkerRegistry>(typeof(int)));
        await Assert.That(provider.GetRequiredKeyedService<AutoscalingEngine>(typeof(string)))
            .IsNotSameReferenceAs(provider.GetRequiredKeyedService<AutoscalingEngine>(typeof(int)));
    }

    /// <summary>The host first drains, then signals abort only when its deadline expires.</summary>
    [Test]
    public async Task HostDeadlineAbortsAfterAttemptingDrain()
    {
        var queue = Substitute.For<IWorkOrchestrator<string>>();
        using var deadline = new CancellationTokenSource();
        await deadline.CancelAsync().ConfigureAwait(false);
        queue.DrainAsync(deadline.Token).Returns(Task.FromCanceled(deadline.Token));
        var host = new WorkOrchestratorHostedService<string>(queue, NullLogger<WorkOrchestratorHostedService<string>>.Instance);
        await host.StopAsync(deadline.Token).ConfigureAwait(false);
        await queue.Received(1).DrainAsync(deadline.Token).ConfigureAwait(false);
        await queue.Received(1).AbortAsync(deadline.Token).ConfigureAwait(false);
    }

    /// <summary>Closing admission is not a capacity rejection through the synchronous decorator path.</summary>
    [Test]
    public async Task SynchronousRejectionAfterDrainDoesNotDeadLetter()
    {
        await using var queue = Create(DispatchStrategy.PriorityExact);
        var dlq = Substitute.For<IDeadLetterQueue<string>>();
        var wrapper = new Bifrost.Decorators.RejectionRoutingOrchestrator<string>(queue, dlq, null,
            NullLogger<Bifrost.Decorators.RejectionRoutingOrchestrator<string>>.Instance);
        await queue.DrainAsync().ConfigureAwait(false);
        await Assert.That(wrapper.Enqueue("closed").Reason).IsEqualTo(RejectionReason.Shutdown);
        await Assert.That(wrapper.TryEnqueue("also closed")).IsFalse();
        await Assert.That(dlq.ReceivedCalls().Any()).IsFalse();
    }

    /// <summary>Completion waits for an admitted producer before letting readers terminate.</summary>
    [Test]
    public async Task RelaxedCompletionWaitsForInFlightPublication()
    {
        using var queue = new ConcurrentPriorityWorkQueue<string>(10, new PriorityDispatchOptions(), TimeProvider.System.TimestampFrequency);
        var field = typeof(ConcurrentPriorityWorkQueue<string>).GetField("_queue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var storage = (Bifrost.Concurrency.ConcurrentPriorityQueue<WorkEnvelope<string>, long>)field.GetValue(queue)!;
        var admission = typeof(ConcurrentPriorityWorkQueue<string>).GetField("_admissionState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var stripes = storage.SubQueuesForTest;
        Task<EnqueueResult>? producer = null;
        Task? closing = null;
        Task? repeated = null;
        bool entered = false, closed = false, waited = false;
        foreach (var stripe in stripes) stripe.SyncLock.Enter();
        try
        {
            producer = Task.Run(() => queue.Enqueue(new WorkEnvelope<string>("accepted", WorkClass.Interactive, 0)));
            entered = SpinWait.SpinUntil(() => (int)admission.GetValue(queue)! == 1, TimeSpan.FromSeconds(5));
            closing = Task.Run(queue.Complete);
            closed = SpinWait.SpinUntil(() => (int)admission.GetValue(queue)! < 0, TimeSpan.FromSeconds(5));
            repeated = Task.Run(queue.Complete);
            waited = !closing.IsCompleted;
        }
        finally
        {
            foreach (var stripe in stripes) stripe.SyncLock.Exit();
        }

        await Task.WhenAll(producer!, closing!, repeated!).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(entered && closed && waited).IsTrue();
        await Assert.That((await producer!.ConfigureAwait(false)).IsAccepted).IsTrue();
        await Assert.That(queue.Enqueue(new WorkEnvelope<string>("closed", WorkClass.Interactive, 0)).Reason).IsEqualTo(RejectionReason.Shutdown);
        await Assert.That(queue.TryDequeue(out var item)).IsTrue();
        await Assert.That(item.Work).IsEqualTo("accepted");
        await Assert.That(await queue.WaitToDequeueAsync(CancellationToken.None).ConfigureAwait(false)).IsFalse();
    }

    /// <summary>A throwing abort callback cannot authorize disposal before a handler exits.</summary>
    [Test]
    public async Task CancellationCallbackFailureStillWaitsForWorkersBeforeDisposal()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = Substitute.For<IWorkHandler<string>>();
        handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            started.TrySetResult();
            return new ValueTask(release.Task);
        });
        var queue = Create(DispatchStrategy.PriorityExact, handler);
        using var registration = queue.GetShutdownToken().Register(() =>
        {
            callback.TrySetResult();
            throw new InvalidOperationException("Injected cancellation callback failure.");
        });
        queue.Enqueue("work");
        Task worker = queue.CreateWorkerFunction()("dynamic", CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Task disposal = queue.DisposeAsync().AsTask();
        try
        {
            await callback.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await Assert.That(disposal.IsCompleted).IsFalse();
            await Assert.That(((LockingPriorityWorkQueue<string>)queue.WorkQueue).IsDisposedForTest).IsFalse();
        }
        finally
        {
            release.TrySetResult();
            await worker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        await Assert.That(async () => await disposal.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)).Throws<AggregateException>();
        await Assert.That(((LockingPriorityWorkQueue<string>)queue.WorkQueue).IsDisposedForTest).IsTrue();
    }

    private static WorkOrchestrator<string> Create(DispatchStrategy ordering, IWorkHandler<string>? handler = null,
        Func<string, WorkClass>? classifier = null)
        => new(handler ?? Substitute.For<IWorkHandler<string>>(), Options.Create(new WorkOrchestratorOptions
        {
            Capacity = 10, WorkerCount = 0, DispatchStrategy = ordering, FullMode = QueueFullMode.Reject,
        }), NullLogger<WorkOrchestrator<string>>.Instance, classifier: classifier);
}
