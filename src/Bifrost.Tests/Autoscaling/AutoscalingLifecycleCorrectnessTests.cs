// =============================================================================
// <copyright file="AutoscalingLifecycleCorrectnessTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling;
using Bifrost.Core;
using Bifrost.Core.Events;
using Bifrost.Decorators;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

namespace Bifrost.Tests.Autoscaling;

/// <summary>
/// Deterministic correctness-by-construction tests for <see cref="AutoscalingEngine"/>,
/// <see cref="WorkerInfo"/>, <see cref="WorkerRegistry"/>, and
/// <see cref="AutoscalingOrchestrator{TWork}"/>.
/// </summary>
public sealed class AutoscalingLifecycleCorrectnessTests
{
    /// <summary>
    /// Verifies that <see cref="AutoscalingEngine.EvaluateScaling()"/> on a clock at
    /// <see cref="DateTimeOffset.MinValue"/> does not falsely trigger the cooldown period
    /// on the very first scaling evaluation.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AutoscalingEngine_FirstEvaluationAtMinValueClock_IsNotBlockedByCooldown()
    {
        var minTime = new FakeTimeProvider(DateTimeOffset.MinValue);
        var coordinator = Substitute.For<IAutoscalingCoordinator>();
        coordinator.ActiveWorkerCount.Returns(2);
        coordinator.GetUtilizationRatio().Returns(0.95);

        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 1,
            MaxWorkers = 10,
            HighWatermark = 0.8,
            LowWatermark = 0.3,
            ScaleUpStep = 2,
            CooldownPeriod = TimeSpan.FromHours(1),
        });

        using var engine = new AutoscalingEngine(
            options,
            coordinator,
            NullLogger<AutoscalingEngine>.Instance,
            minTime);

        var decision = engine.EvaluateScaling();
        await Assert.That(decision.Action).IsEqualTo(ScalingAction.ScaleUp);
    }

    /// <summary>
    /// Verifies that <see cref="WorkerInfo.TryRequestStopIfIdle"/> atomically refuses
    /// to stop a busy worker and prevents concurrent busy/stop-if-idle from both
    /// claiming the worker, while still cancelling <see cref="WorkerInfo.RetirementToken"/>
    /// when it succeeds.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WorkerInfo_TryRequestStopIfIdle_NeverStopsBusyWorker()
    {
        var info = new WorkerInfo("w1");
        try
        {
            info.MarkBusy();

            await Assert.That(info.TryRequestStopIfIdle()).IsFalse();
            await Assert.That(info.StopRequested).IsFalse();
            await Assert.That(info.RetirementToken.IsCancellationRequested).IsFalse();

            info.MarkIdle();
            await Assert.That(info.TryRequestStopIfIdle()).IsTrue();
            await Assert.That(info.StopRequested).IsTrue();
            await Assert.That(info.RetirementToken.IsCancellationRequested).IsTrue();
            await Assert.That(info.TryRequestStopIfIdle()).IsFalse();
        }
        finally
        {
            info.DisposeStopSource();
        }
    }

    /// <summary>
    /// Verifies that <see cref="AutoscalingOrchestrator{TWork}.RequestScaleDownAsync"/>
    /// backed by a real <see cref="WorkerRegistry"/> stops only idle workers and never
    /// stops busy workers.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AutoscalingOrchestrator_ScaleDown_NeverStopsBusyWorker()
    {
        var inner = Substitute.For<IWorkOrchestrator<string>>();
        var registry = new WorkerRegistry();
        var metrics = Substitute.For<IWorkerMetrics>();
        var options = Options.Create(new AutoscalingOptions { Enabled = true });
        var orchestrator = new AutoscalingOrchestrator<string>(
            inner, registry, metrics, options, NullLogger<AutoscalingOrchestrator<string>>.Instance);

        using var shutdownCts = new CancellationTokenSource();
        var busyWorker = await registry.CreateWorkerAsync(
            "busy",
            async (_, ct) => await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false),
            shutdownCts.Token).ConfigureAwait(false);
        busyWorker.MarkBusy();

        var idleWorker = await registry.CreateWorkerAsync(
            "idle",
            async (_, ct) => await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false),
            shutdownCts.Token).ConfigureAwait(false);
        idleWorker.MarkIdle();

        await orchestrator.RequestScaleDownAsync(5).ConfigureAwait(false);

        await Assert.That(idleWorker.StopRequested).IsTrue();
        await Assert.That(busyWorker.StopRequested).IsFalse();

        await shutdownCts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that <see cref="WorkerRegistry.CreateWorkerAsync"/> with an
    /// already-cancelled token throws <see cref="OperationCanceledException"/> without
    /// registering the worker.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WorkerRegistry_CreateWorkerWithCancelledToken_DoesNotRegister()
    {
        var registry = new WorkerRegistry();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync().ConfigureAwait(false);

        await Assert.That(async () => await registry.CreateWorkerAsync(
                "w1",
                (_, _) => Task.CompletedTask,
                cts.Token).ConfigureAwait(false))
            .Throws<OperationCanceledException>();

        await Assert.That(registry.ActiveWorkerCount).IsEqualTo(0);
    }
}
