// =============================================================================
// <copyright file="AutoscalingEngineEventsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling;
using Bifrost.Core.Events;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TUnit.Core;

namespace Bifrost.Tests.Autoscaling;

/// <summary>
/// Tests for <see cref="AutoscalingEngine"/> ScalingDecisionMade event.
/// </summary>
[Property("Category", "Unit")]
public class AutoscalingEngineEventsTests
{
    /// <summary>
    /// Verifies that ScalingDecisionMade event is raised after evaluation.
    /// </summary>
    /// <remarks>
    /// The 200 ms delay lets at least one evaluation occur.
    /// </remarks>
    [Test]
    public async Task ScalingDecisionMade_AfterEvaluation_RaisesEvent()
    {
        var options = CreateOptions(checkInterval: TimeSpan.FromMilliseconds(50));
        var coordinator = CreateMockCoordinator(activeWorkers: 2, utilization: 0.5);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        ScalingDecisionEventArgs? capturedArgs = null;
        engine.ScalingDecisionMade += (sender, args) => capturedArgs = args;

        await engine.StartAsync().ConfigureAwait(false);
        await Task.Delay(200).ConfigureAwait(false);
        await engine.StopAsync().ConfigureAwait(false);

        await Assert.That(capturedArgs).IsNotNull();
        await Assert.That(capturedArgs!.Decision.CurrentWorkers).IsEqualTo(2);
    }

    /// <summary>
    /// Verifies that event args contain correlation ID.
    /// </summary>
    [Test]
    public async Task ScalingDecisionMade_ContainsCorrelationId()
    {
        var options = CreateOptions(checkInterval: TimeSpan.FromMilliseconds(50));
        var coordinator = CreateMockCoordinator(activeWorkers: 2, utilization: 0.5);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        ScalingDecisionEventArgs? capturedArgs = null;
        engine.ScalingDecisionMade += (sender, args) => capturedArgs = args;

        await engine.StartAsync().ConfigureAwait(false);
        await Task.Delay(200).ConfigureAwait(false);
        await engine.StopAsync().ConfigureAwait(false);

        await Assert.That(capturedArgs).IsNotNull();
        await Assert.That(capturedArgs!.CorrelationId).IsNotEmpty();
    }

    /// <summary>
    /// Verifies that each event has a unique correlation ID.
    /// </summary>
    /// <remarks>
    /// The test polls until at least 2 evaluations occur, with a generous timeout for CI. Each of
    /// those evaluations must have a unique correlation ID.
    /// </remarks>
    [Test]
    public async Task ScalingDecisionMade_EachEventHasUniqueCorrelationId()
    {
        var options = CreateOptions(checkInterval: TimeSpan.FromMilliseconds(50));
        var coordinator = CreateMockCoordinator(activeWorkers: 2, utilization: 0.5);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        var correlationIds = new List<string>();
        engine.ScalingDecisionMade += (_, args) => correlationIds.Add(args.CorrelationId);

        await engine.StartAsync().ConfigureAwait(false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (correlationIds.Count < 2 && !cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(50, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await engine.StopAsync().ConfigureAwait(false);

        await Assert.That(correlationIds.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(correlationIds.Distinct().Count()).IsEqualTo(correlationIds.Count);
    }

    /// <summary>
    /// Verifies that event contains the scaling decision.
    /// </summary>
    /// <remarks>
    /// The high utilization triggers a scale up, so the target is 2 + 2 (the step) = 4.
    /// </remarks>
    [Test]
    public async Task ScalingDecisionMade_ContainsScalingDecision()
    {
        var options = CreateOptions(checkInterval: TimeSpan.FromMilliseconds(50));
        var coordinator = CreateMockCoordinator(activeWorkers: 2, utilization: 0.9);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        ScalingDecisionEventArgs? capturedArgs = null;
        engine.ScalingDecisionMade += (_, args) => capturedArgs = args;

        await engine.StartAsync().ConfigureAwait(false);
        await Task.Delay(200).ConfigureAwait(false);
        await engine.StopAsync().ConfigureAwait(false);

        await Assert.That(capturedArgs).IsNotNull();
        await Assert.That(capturedArgs!.Decision.Action).IsEqualTo(ScalingAction.ScaleUp);
        await Assert.That(capturedArgs.Decision.CurrentWorkers).IsEqualTo(2);
        await Assert.That(capturedArgs.Decision.TargetWorkers).IsEqualTo(4);
    }

    /// <summary>
    /// Verifies that no event is raised when engine is stopped.
    /// </summary>
    /// <remarks>
    /// The test never starts the engine.
    /// </remarks>
    [Test]
    public async Task ScalingDecisionMade_WhenStopped_NoEventRaised()
    {
        var options = CreateOptions(checkInterval: TimeSpan.FromMilliseconds(50));
        var coordinator = CreateMockCoordinator(activeWorkers: 2, utilization: 0.5);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        var eventCount = 0;
        engine.ScalingDecisionMade += (_, _) => Interlocked.Increment(ref eventCount);

        await Task.Delay(100).ConfigureAwait(false);

        await Assert.That(eventCount).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies that ScalingDecisionEventArgs is a sealed record.
    /// </summary>
    [Test]
    public async Task ScalingDecisionEventArgs_IsSealedRecord()
    {
        var type = typeof(ScalingDecisionEventArgs);

        await Assert.That(type.IsSealed).IsTrue();
    }

    /// <summary>
    /// Verifies that a faulted scaling decision execution logs the error (M8).
    /// </summary>
    /// <remarks>
    /// The high utilization triggers a scale up, and the scale up throws. The 300 ms delay lets the
    /// evaluation and the fault handling occur. The <c>ContinueWith</c> fault handler must log the
    /// error.
    /// </remarks>
    [Test]
    public async Task ExecuteScalingDecisionAsync_OnFault_LogsError()
    {
        var options = CreateOptions(checkInterval: TimeSpan.FromMilliseconds(50));
        var coordinator = Substitute.For<IAutoscalingCoordinator>();
        coordinator.ActiveWorkerCount.Returns(2);
        coordinator.GetUtilizationRatio().Returns(0.9);
        coordinator.RequestScaleUpAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Scaling fault"));

        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<AutoscalingEngine>>();
        var engine = new AutoscalingEngine(options, coordinator, logger);

        await engine.StartAsync().ConfigureAwait(false);
        await Task.Delay(300).ConfigureAwait(false);
        await engine.StopAsync().ConfigureAwait(false);

        logger.Received().Log(
            Microsoft.Extensions.Logging.LogLevel.Error,
            Arg.Any<Microsoft.Extensions.Logging.EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// Verifies that StopAsync drains pending scaling tasks (M8).
    /// </summary>
    /// <remarks>
    /// The high utilization triggers a scale up with a slow handler. The test waits until at least
    /// one scale up starts. <c>scaleUpStarted</c> fires inside the mock, before the callback does
    /// <c>_pendingScalingTasks.Add</c>, so the 100 ms delay lets the callback add the task to the
    /// pending bag. <c>StopAsync</c> must drain the pending tasks, so the scaling task is complete
    /// when it returns.
    /// </remarks>
    [Test]
    public async Task StopAsync_DrainsPendingScalingTasks()
    {
        var options = CreateOptions(checkInterval: TimeSpan.FromMilliseconds(50));
        var coordinator = Substitute.For<IAutoscalingCoordinator>();
        coordinator.ActiveWorkerCount.Returns(2);
        coordinator.GetUtilizationRatio().Returns(0.9);

        var scaleUpStarted = new TaskCompletionSource();
        var scaleUpCompleted = new TaskCompletionSource();
        coordinator.RequestScaleUpAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                scaleUpStarted.TrySetResult();
                await Task.Delay(500).ConfigureAwait(false);
                scaleUpCompleted.TrySetResult();
            });

        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        await engine.StartAsync().ConfigureAwait(false);
        await scaleUpStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Task.Delay(100).ConfigureAwait(false);
        await engine.StopAsync().ConfigureAwait(false);

        await Assert.That(scaleUpCompleted.Task.IsCompleted).IsTrue();
    }

    private static IOptions<AutoscalingOptions> CreateOptions(TimeSpan? checkInterval = null)
    {
        return Options.Create(new AutoscalingOptions
        {
            MinWorkers = 1,
            MaxWorkers = 10,
            HighWatermark = 0.8,
            LowWatermark = 0.3,
            ScaleUpStep = 2,
            ScaleDownStep = 1,
            CheckInterval = checkInterval ?? TimeSpan.FromSeconds(5),
            CooldownPeriod = TimeSpan.Zero,
        });
    }

    private static IAutoscalingCoordinator CreateMockCoordinator(int activeWorkers, double utilization = 0.5)
    {
        var coordinator = Substitute.For<IAutoscalingCoordinator>();
        coordinator.ActiveWorkerCount.Returns(activeWorkers);
        coordinator.GetUtilizationRatio().Returns(utilization);
        return coordinator;
    }
}