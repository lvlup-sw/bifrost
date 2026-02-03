// =============================================================================
// <copyright file="AutoscalingEngineEventsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling;
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
        await Task.Delay(350).ConfigureAwait(false); // Wait for multiple evaluations
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
