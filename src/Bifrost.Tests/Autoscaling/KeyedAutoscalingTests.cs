// =============================================================================
// <copyright file="KeyedAutoscalingTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling;
using Bifrost.Core;
using Bifrost.Core.Events;
using Bifrost.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using TUnit.Core;

namespace Bifrost.Tests.Autoscaling;

/// <summary>
/// Tests that autoscaling scales up on the dispatchable backlog of a keyed orchestrator, not on
/// its total pending depth.
/// </summary>
/// <remarks>
/// <para>
/// The DI tests use one worker, a capacity of 8, a per-key concurrency of 1, and a high watermark
/// of 0.25. Key A runs one item and has three items blocked behind it. Under the total pending
/// depth that backlog reads as 3/8 = 0.375, which is above the watermark and would scale up.
/// Under the dispatchable backlog it reads as 0. The low watermark is 0 so that the engine never
/// decides to scale down in these tests.
/// </para>
/// <para>
/// The handler is gated, so the queue state cannot change while the test reads the coordinator
/// and asks the engine for a decision. Bounded waits only guard against hangs.
/// </para>
/// </remarks>
[Property("Category", "Unit")]
public class KeyedAutoscalingTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A backlog blocked behind one hot key at its cap triggers no scale-up.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task BlockedOnlyBacklog_DoesNotScaleUp()
    {
        await using var fixture = KeyedFixture.Create();
        await fixture.AdmitAndWaitAsync("A:1");
        fixture.Admit("A:2", "A:3", "A:4");

        var decision = fixture.Engine.EvaluateScaling();

        await Assert.That(fixture.Coordinator.PendingWorkCount).IsEqualTo(3);
        await Assert.That(fixture.Coordinator.GetUtilizationRatio()).IsEqualTo(0.0);
        await Assert.That(decision.Action).IsEqualTo(ScalingAction.None);
    }

    /// <summary>
    /// Ready items across other keys still scale up, and the blocked items beside them do not add
    /// to the ratio.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DispatchableBacklogAcrossKeys_ScalesUp()
    {
        await using var fixture = KeyedFixture.Create();
        await fixture.AdmitAndWaitAsync("A:1");
        fixture.Admit("A:2", "A:3", "A:4", "B:1", "C:1", "D:1");

        var decision = fixture.Engine.EvaluateScaling();

        await Assert.That(fixture.Coordinator.PendingWorkCount).IsEqualTo(6);
        await Assert.That(fixture.Coordinator.GetUtilizationRatio()).IsEqualTo(3.0 / 8);
        await Assert.That(decision.Action).IsEqualTo(ScalingAction.ScaleUp);
    }

    /// <summary>The coordinator divides the dispatchable signal, not the pending count, by capacity.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Coordinator_WithDispatchableSignal_UsesSignalForRatio()
    {
        var orchestrator = Substitute.For<IWorkOrchestrator<string>>();
        orchestrator.Capacity.Returns(10);
        orchestrator.PendingCount.Returns(8);

        var coordinator = new AutoscalingCoordinator<string>(orchestrator, Substitute.For<IWorkerRegistry>(), null, static () => 2);

        await Assert.That(coordinator.GetUtilizationRatio()).IsEqualTo(0.2);
        await Assert.That(coordinator.PendingWorkCount).IsEqualTo(8);
    }

    /// <summary>A FIFO orchestrator gets no dispatchable signal, so the ratio stays pending over capacity.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task FifoOrchestrator_RatioUsesPendingCount()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkOrchestrator<string>(static o =>
            {
                o.Capacity = 8;
                o.WorkerCount = 1;
            })
            .WithHandler(async (_, ct) =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(ct);
            })
            .WithAutoscaling()
            .Build();
        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        var coordinator = provider.GetRequiredKeyedService<IAutoscalingCoordinator>(typeof(string));

        await orchestrator.EnqueueAsync("first");
        await started.Task.WaitAsync(WaitTimeout);
        await orchestrator.EnqueueAsync("second");
        await orchestrator.EnqueueAsync("third");

        await Assert.That(coordinator.GetUtilizationRatio()).IsEqualTo(2.0 / 8);

        release.TrySetResult();
        await orchestrator.DrainAsync().WaitAsync(WaitTimeout);
        await orchestrator.DisposeAsync();
    }

    /// <summary>A keyed orchestrator with autoscaling, built through the public DI path.</summary>
    private sealed class KeyedFixture : IAsyncDisposable
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, TaskCompletionSource> _started = [];
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ServiceProvider _provider = null!;

        public IWorkOrchestrator<string> Orchestrator { get; private set; } = null!;

        public IAutoscalingCoordinator Coordinator { get; private set; } = null!;

        public AutoscalingEngine Engine { get; private set; } = null!;

        public static KeyedFixture Create()
        {
            var fixture = new KeyedFixture();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddWorkOrchestrator<string>(static o =>
                {
                    o.Capacity = 8;
                    o.WorkerCount = 1;
                })
                .WithKeyedDispatch(static w => w.Split(':')[0], maxPendingPerKey: 8, maxConcurrencyPerKey: 1)
                .WithHandler(fixture.HandleAsync)
                .WithAutoscaling(new AutoscalingOptions
                {
                    MinWorkers = 1,
                    MaxWorkers = 4,
                    HighWatermark = 0.25,
                    LowWatermark = 0.0,
                })
                .Build();
            fixture._provider = services.BuildServiceProvider();
            fixture.Orchestrator = fixture._provider.GetRequiredService<IWorkOrchestrator<string>>();
            fixture.Coordinator = fixture._provider.GetRequiredKeyedService<IAutoscalingCoordinator>(typeof(string));
            fixture.Engine = fixture._provider.GetRequiredKeyedService<AutoscalingEngine>(typeof(string));
            return fixture;
        }

        public async Task AdmitAndWaitAsync(string work)
        {
            Admit(work);
            Task started;
            lock (_gate) started = Started(work).Task;
            await started.WaitAsync(WaitTimeout);
        }

        public void Admit(params string[] work)
        {
            foreach (var item in work)
            {
                if (!Orchestrator.Enqueue(item).IsAccepted)
                    throw new InvalidOperationException($"Setup admission of {item} was rejected.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            _release.TrySetResult();
            await Orchestrator.DrainAsync().WaitAsync(WaitTimeout);
            await _provider.DisposeAsync();
        }

        private async ValueTask HandleAsync(string work, CancellationToken ct)
        {
            lock (_gate) Started(work).TrySetResult();
            await _release.Task.WaitAsync(ct);
        }

        private TaskCompletionSource Started(string work)
        {
            if (!_started.TryGetValue(work, out var signal))
            {
                signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _started[work] = signal;
            }

            return signal;
        }
    }
}
