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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TUnit.Core;

namespace Bifrost.Tests.Autoscaling;

/// <summary>
/// Tests that autoscaling scales up on the dispatchable backlog of a keyed orchestrator, and
/// scales down on its total pending depth.
/// </summary>
/// <remarks>
/// <para>
/// The scale-up DI tests use one worker, a capacity of 8, a per-key concurrency of 1, a high
/// watermark of 0.25, and a low watermark of 0.1. Key A runs one item and has three items blocked
/// behind it. Under the total pending depth that backlog reads as 3/8 = 0.375, which is above the
/// high watermark and would scale up. Under the dispatchable backlog it reads as 0.
/// </para>
/// <para>
/// The scale-down DI test uses the default watermarks. Three hot keys each run one item on three
/// workers and have two items blocked behind them. The dispatchable backlog is 0, which is below
/// the low watermark, but the total pending depth is 6/10 = 0.6, so the engine keeps the workers.
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

    private static readonly AutoscalingOptions ScaleUpOptions = new()
    {
        MinWorkers = 1,
        MaxWorkers = 4,
        HighWatermark = 0.25,
        LowWatermark = 0.1,
    };

    /// <summary>A backlog blocked behind one hot key at its cap triggers no scale-up.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task BlockedOnlyBacklog_DoesNotScaleUp()
    {
        await using var fixture = KeyedFixture.Create(capacity: 8, ScaleUpOptions);
        await fixture.AdmitAndWaitAsync("A:1");
        fixture.Admit("A:2", "A:3", "A:4");

        var decision = fixture.Engine.EvaluateScaling();

        await Assert.That(fixture.Coordinator.PendingWorkCount).IsEqualTo(3);
        await Assert.That(fixture.Coordinator.GetUtilizationRatio()).IsEqualTo(3.0 / 8);
        await Assert.That(fixture.ScaleUpRatio).IsEqualTo(0.0);
        await Assert.That(decision.Action).IsEqualTo(ScalingAction.None);
    }

    /// <summary>
    /// Ready items across other keys still scale up, and the blocked items beside them do not add
    /// to the scale-up ratio.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DispatchableBacklogAcrossKeys_ScalesUp()
    {
        await using var fixture = KeyedFixture.Create(capacity: 8, ScaleUpOptions);
        await fixture.AdmitAndWaitAsync("A:1");
        fixture.Admit("A:2", "A:3", "A:4", "B:1", "C:1", "D:1");

        var decision = fixture.Engine.EvaluateScaling();

        await Assert.That(fixture.Coordinator.PendingWorkCount).IsEqualTo(6);
        await Assert.That(fixture.Coordinator.GetUtilizationRatio()).IsEqualTo(6.0 / 8);
        await Assert.That(fixture.ScaleUpRatio).IsEqualTo(3.0 / 8);
        await Assert.That(decision.Action).IsEqualTo(ScalingAction.ScaleUp);
        await Assert.That(decision.UtilizationRatio).IsEqualTo(3.0 / 8);
    }

    /// <summary>
    /// With the default watermarks, hot keys at their cap with a blocked backlog do not scale the
    /// pool down, even though no queued item is dispatchable.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task HotKeysAtCapWithBlockedBacklog_DoNotScaleDown()
    {
        await using var fixture = KeyedFixture.Create(capacity: 10, new AutoscalingOptions { MinWorkers = 1, MaxWorkers = 4 });
        await fixture.Coordinator.RequestScaleUpAsync(2);
        await fixture.AdmitAndWaitAsync("A:1");
        await fixture.AdmitAndWaitAsync("B:1");
        await fixture.AdmitAndWaitAsync("C:1");
        fixture.Admit("A:2", "A:3", "B:2", "B:3", "C:2", "C:3");

        var decision = fixture.Engine.EvaluateScaling();

        await Assert.That(fixture.Engine.EffectiveMinWorkers).IsEqualTo(1);
        await Assert.That(fixture.Coordinator.ActiveWorkerCount).IsEqualTo(3);
        await Assert.That(fixture.ScaleUpRatio).IsEqualTo(0.0);
        await Assert.That(fixture.Coordinator.GetUtilizationRatio()).IsEqualTo(0.6);
        await Assert.That(decision.Action).IsEqualTo(ScalingAction.None);
    }

    /// <summary>
    /// The coordinator divides the dispatchable signal by capacity for scale-up, and the pending
    /// count by capacity for the public ratio.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Coordinator_WithDispatchableSignal_UsesSignalForScaleUpRatioOnly()
    {
        var orchestrator = Substitute.For<IWorkOrchestrator<string>>();
        orchestrator.Capacity.Returns(10);
        orchestrator.PendingCount.Returns(8);

        var coordinator = new AutoscalingCoordinator<string>(orchestrator, Substitute.For<IWorkerRegistry>(), null, static () => 2);

        await Assert.That(coordinator.GetScaleUpUtilizationRatio()).IsEqualTo(0.2);
        await Assert.That(coordinator.GetUtilizationRatio()).IsEqualTo(0.8);
        await Assert.That(coordinator.PendingWorkCount).IsEqualTo(8);
    }

    /// <summary>
    /// Ten hot keys at their cap with 800 of 1000 items blocked do not scale down ten busy workers.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Engine_BlockedBacklogAboveLowWatermark_DoesNotScaleDown()
    {
        var engine = EngineFor(pending: 800, dispatchable: 0);

        var decision = engine.EvaluateScaling();

        await Assert.That(decision.Action).IsEqualTo(ScalingAction.None);
        await Assert.That(decision.UtilizationRatio).IsEqualTo(0.8);
    }

    /// <summary>A keyed backlog below the low watermark still scales down.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Engine_KeyedBacklogBelowLowWatermark_ScalesDown()
    {
        var engine = EngineFor(pending: 100, dispatchable: 0);

        var decision = engine.EvaluateScaling();

        await Assert.That(decision.Action).IsEqualTo(ScalingAction.ScaleDown);
        await Assert.That(decision.TargetWorkers).IsEqualTo(9);
    }

    /// <summary>
    /// Builds an engine with default watermarks over a keyed coordinator with a capacity of 1000.
    /// The engine starts with one worker, so its minimum is 1, and then sees ten.
    /// </summary>
    private static AutoscalingEngine EngineFor(int pending, int dispatchable)
    {
        var orchestrator = Substitute.For<IWorkOrchestrator<string>>();
        orchestrator.Capacity.Returns(1000);
        orchestrator.PendingCount.Returns(pending);
        orchestrator.ActiveWorkers.Returns(1, 10);
        var coordinator = new AutoscalingCoordinator<string>(orchestrator, Substitute.For<IWorkerRegistry>(), null, () => dispatchable);
        return new AutoscalingEngine(
            Options.Create(new AutoscalingOptions { MinWorkers = 1, MaxWorkers = 20 }),
            coordinator,
            NullLogger<AutoscalingEngine>.Instance);
    }

    /// <summary>
    /// A FIFO orchestrator gets no dispatchable signal: no backlog source is registered, and both
    /// ratios stay pending over capacity.
    /// </summary>
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
        await Assert.That(((AutoscalingCoordinator<string>)coordinator).GetScaleUpUtilizationRatio()).IsEqualTo(2.0 / 8);
        await Assert.That(provider.GetService<DispatchableBacklogSource<string>>()).IsNull();

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

        public double ScaleUpRatio => ((AutoscalingCoordinator<string>)Coordinator).GetScaleUpUtilizationRatio();

        public static KeyedFixture Create(int capacity, AutoscalingOptions options)
        {
            var fixture = new KeyedFixture();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddWorkOrchestrator<string>(o =>
                {
                    o.Capacity = capacity;
                    o.WorkerCount = 1;
                })
                .WithKeyedDispatch(static w => w.Split(':')[0], maxPendingPerKey: 8, maxConcurrencyPerKey: 1)
                .WithHandler(fixture.HandleAsync)
                .WithAutoscaling(options)
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
