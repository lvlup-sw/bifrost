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
    [Test]
    public async Task ScalingDecisionMade_AfterEvaluation_RaisesEvent()
    {
        // Arrange
        var options = CreateOptions(checkInterval: TimeSpan.FromMilliseconds(50));
        var coordinator = CreateMockCoordinator(activeWorkers: 2, utilization: 0.5);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        ScalingDecisionEventArgs? capturedArgs = null;
        engine.ScalingDecisionMade += (sender, args) => capturedArgs = args;

        // Act
        await engine.StartAsync().ConfigureAwait(false);
        await Task.Delay(200).ConfigureAwait(false); // Wait for at least one evaluation
        await engine.StopAsync().ConfigureAwait(false);

        // Assert
        await Assert.That(capturedArgs).IsNotNull();
        await Assert.That(capturedArgs!.Decision.CurrentWorkers).IsEqualTo(2);
    }

    /// <summary>
    /// Verifies that event args contain correlation ID.
    /// </summary>
    [Test]
    public async Task ScalingDecisionMade_ContainsCorrelationId()
    {
        // Arrange
        var options = CreateOptions(checkInterval: TimeSpan.FromMilliseconds(50));
        var coordinator = CreateMockCoordinator(activeWorkers: 2, utilization: 0.5);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        ScalingDecisionEventArgs? capturedArgs = null;
        engine.ScalingDecisionMade += (sender, args) => capturedArgs = args;

        // Act
        await engine.StartAsync().ConfigureAwait(false);
        await Task.Delay(200).ConfigureAwait(false);
        await engine.StopAsync().ConfigureAwait(false);

        // Assert
        await Assert.That(capturedArgs).IsNotNull();
        await Assert.That(capturedArgs!.CorrelationId).IsNotEmpty();
    }

    /// <summary>
    /// Verifies that each event has a unique correlation ID.
    /// </summary>
    [Test]
    public async Task ScalingDecisionMade_EachEventHasUniqueCorrelationId()
    {
        // Arrange
        var options = CreateOptions(checkInterval: TimeSpan.FromMilliseconds(50));
        var coordinator = CreateMockCoordinator(activeWorkers: 2, utilization: 0.5);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        var correlationIds = new List<string>();
        engine.ScalingDecisionMade += (_, args) => correlationIds.Add(args.CorrelationId);

        // Act
        await engine.StartAsync().ConfigureAwait(false);

        // Poll until we have at least 2 evaluations (generous timeout for CI)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (correlationIds.Count < 2 && !cts.IsCancellationRequested)
        {
            await Task.Delay(50, cts.Token).ConfigureAwait(false);
        }

        await engine.StopAsync().ConfigureAwait(false);

        // Assert - Should have at least 2 evaluations with unique IDs
        await Assert.That(correlationIds.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(correlationIds.Distinct().Count()).IsEqualTo(correlationIds.Count);
    }

    /// <summary>
    /// Verifies that event contains the scaling decision.
    /// </summary>
    [Test]
    public async Task ScalingDecisionMade_ContainsScalingDecision()
    {
        // Arrange - High utilization to trigger scale up
        var options = CreateOptions(checkInterval: TimeSpan.FromMilliseconds(50));
        var coordinator = CreateMockCoordinator(activeWorkers: 2, utilization: 0.9);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        ScalingDecisionEventArgs? capturedArgs = null;
        engine.ScalingDecisionMade += (_, args) => capturedArgs = args;

        // Act
        await engine.StartAsync().ConfigureAwait(false);
        await Task.Delay(200).ConfigureAwait(false);
        await engine.StopAsync().ConfigureAwait(false);

        // Assert
        await Assert.That(capturedArgs).IsNotNull();
        await Assert.That(capturedArgs!.Decision.Action).IsEqualTo(ScalingAction.ScaleUp);
        await Assert.That(capturedArgs.Decision.CurrentWorkers).IsEqualTo(2);
        await Assert.That(capturedArgs.Decision.TargetWorkers).IsEqualTo(4); // 2 + 2 (step)
    }

    /// <summary>
    /// Verifies that no event is raised when engine is stopped.
    /// </summary>
    [Test]
    public async Task ScalingDecisionMade_WhenStopped_NoEventRaised()
    {
        // Arrange
        var options = CreateOptions(checkInterval: TimeSpan.FromMilliseconds(50));
        var coordinator = CreateMockCoordinator(activeWorkers: 2, utilization: 0.5);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        var eventCount = 0;
        engine.ScalingDecisionMade += (_, _) => Interlocked.Increment(ref eventCount);

        // Act - Never start the engine
        await Task.Delay(100).ConfigureAwait(false);

        // Assert
        await Assert.That(eventCount).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies that ScalingDecisionEventArgs is a sealed record.
    /// </summary>
    [Test]
    public async Task ScalingDecisionEventArgs_IsSealedRecord()
    {
        // Arrange
        var type = typeof(ScalingDecisionEventArgs);

        // Assert
        await Assert.That(type.IsSealed).IsTrue();
    }

    /// <summary>
    /// Verifies that a faulted scaling decision execution logs the error (M8).
    /// </summary>
    [Test]
    public async Task ExecuteScalingDecisionAsync_OnFault_LogsError()
    {
        // Arrange - High utilization to trigger scale up
        var options = CreateOptions(checkInterval: TimeSpan.FromMilliseconds(50));
        var coordinator = Substitute.For<IAutoscalingCoordinator>();
        coordinator.ActiveWorkerCount.Returns(2);
        coordinator.GetUtilizationRatio().Returns(0.9);
        coordinator.RequestScaleUpAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Scaling fault"));

        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<AutoscalingEngine>>();
        var engine = new AutoscalingEngine(options, coordinator, logger);

        // Act
        await engine.StartAsync().ConfigureAwait(false);
        await Task.Delay(300).ConfigureAwait(false); // Wait for evaluation + fault handling
        await engine.StopAsync().ConfigureAwait(false);

        // Assert - Error should be logged (via ContinueWith fault handler)
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
    [Test]
    public async Task StopAsync_DrainsPendingScalingTasks()
    {
        // Arrange - High utilization to trigger scale up with a slow handler
        var options = CreateOptions(checkInterval: TimeSpan.FromMilliseconds(50));
        var coordinator = Substitute.For<IAutoscalingCoordinator>();
        coordinator.ActiveWorkerCount.Returns(2);
        coordinator.GetUtilizationRatio().Returns(0.9);

        var scaleUpStarted = new TaskCompletionSource();
        var scaleUpCompleted = false;
        coordinator.RequestScaleUpAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                scaleUpStarted.TrySetResult();
                await Task.Delay(200).ConfigureAwait(false);
                scaleUpCompleted = true;
            });

        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        // Act
        await engine.StartAsync().ConfigureAwait(false);
        // Wait for at least one scale up to start
        await scaleUpStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await engine.StopAsync().ConfigureAwait(false);

        // Assert - The pending scaling task should have been drained
        await Assert.That(scaleUpCompleted).IsTrue();
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