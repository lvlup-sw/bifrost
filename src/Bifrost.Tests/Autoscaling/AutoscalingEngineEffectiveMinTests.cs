// =============================================================================
// <copyright file="AutoscalingEngineEffectiveMinTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TUnit.Core;

namespace Bifrost.Tests.Autoscaling;

/// <summary>
/// Tests for <see cref="AutoscalingEngine"/> EffectiveMinWorkers calculation.
/// </summary>
[Property("Category", "Unit")]
public class AutoscalingEngineEffectiveMinTests
{
    /// <summary>
    /// Verifies that EffectiveMinWorkers equals MinWorkers when initial count matches.
    /// </summary>
    [Test]
    public async Task EffectiveMinWorkers_WhenInitialCountMatchesConfig_EqualsMinWorkers()
    {
        // Arrange
        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 2,
            MaxWorkers = 10,
            CheckInterval = TimeSpan.FromSeconds(5),
        });
        var coordinator = CreateMockCoordinator(activeWorkers: 2);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        // Assert
        await Assert.That(engine.EffectiveMinWorkers).IsEqualTo(2);
    }

    /// <summary>
    /// Verifies that EffectiveMinWorkers uses initial count when greater than config.
    /// </summary>
    [Test]
    public async Task EffectiveMinWorkers_WhenInitialCountGreaterThanConfig_UsesInitialCount()
    {
        // Arrange - Initial workers (5) > configured min (2)
        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 2,
            MaxWorkers = 10,
            CheckInterval = TimeSpan.FromSeconds(5),
        });
        var coordinator = CreateMockCoordinator(activeWorkers: 5);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        // Assert
        await Assert.That(engine.EffectiveMinWorkers).IsEqualTo(5);
    }

    /// <summary>
    /// Verifies that EffectiveMinWorkers uses config when initial count is less.
    /// </summary>
    [Test]
    public async Task EffectiveMinWorkers_WhenInitialCountLessThanConfig_UsesConfig()
    {
        // Arrange - Initial workers (1) < configured min (3)
        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 3,
            MaxWorkers = 10,
            CheckInterval = TimeSpan.FromSeconds(5),
        });
        var coordinator = CreateMockCoordinator(activeWorkers: 1);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        // Assert
        await Assert.That(engine.EffectiveMinWorkers).IsEqualTo(3);
    }

    /// <summary>
    /// Verifies that a warning is logged when there is a mismatch.
    /// </summary>
    [Test]
    public async Task Constructor_WhenMismatch_LogsWarning()
    {
        // Arrange
        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 2,
            MaxWorkers = 10,
            CheckInterval = TimeSpan.FromSeconds(5),
        });
        var coordinator = CreateMockCoordinator(activeWorkers: 5);
        var logger = Substitute.For<ILogger<AutoscalingEngine>>();

        // Act
        _ = new AutoscalingEngine(options, coordinator, logger);

        // Assert - Verify warning was logged
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("5") && o.ToString()!.Contains("2")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());

        // Verify logger was called at least once
        var callCount = logger.ReceivedCalls().Count();
        await Assert.That(callCount).IsGreaterThan(0);
    }

    /// <summary>
    /// Verifies that no warning is logged when initial count matches config.
    /// </summary>
    [Test]
    public async Task Constructor_WhenMatch_NoWarningLogged()
    {
        // Arrange
        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 2,
            MaxWorkers = 10,
            CheckInterval = TimeSpan.FromSeconds(5),
        });
        var coordinator = CreateMockCoordinator(activeWorkers: 2);
        var logger = Substitute.For<ILogger<AutoscalingEngine>>();

        // Act
        var engine = new AutoscalingEngine(options, coordinator, logger);

        // Assert - Verify warning was NOT logged (only informational logs expected)
        logger.DidNotReceive().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());

        // Verify engine was created successfully
        await Assert.That(engine.EffectiveMinWorkers).IsEqualTo(2);
    }

    /// <summary>
    /// Verifies that scale down respects EffectiveMinWorkers.
    /// </summary>
    [Test]
    public async Task EvaluateScaling_ScaleDown_RespectsEffectiveMinWorkers()
    {
        // Arrange - Initial workers (5) > configured min (2), so effective min is 5
        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 2,
            MaxWorkers = 10,
            HighWatermark = 0.8,
            LowWatermark = 0.3,
            ScaleDownStep = 1,
            CheckInterval = TimeSpan.FromSeconds(5),
            CooldownPeriod = TimeSpan.Zero,
        });
        var coordinator = CreateMockCoordinator(activeWorkers: 5, utilization: 0.1);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        // Act - Low utilization, but at effective min
        var decision = engine.EvaluateScaling();

        // Assert - Should not scale below effective min
        await Assert.That(decision.Action).IsEqualTo(ScalingAction.None);
        await Assert.That(decision.Reason).Contains("min");
    }

    /// <summary>
    /// Verifies that scale down targets EffectiveMinWorkers, not raw MinWorkers.
    /// </summary>
    [Test]
    public async Task EvaluateScaling_ScaleDown_ClampsToEffectiveMin()
    {
        // Arrange - Initial workers (5) > configured min (2), so effective min is 5
        // But current workers is 7, so we can scale down to 5
        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 2,
            MaxWorkers = 10,
            HighWatermark = 0.8,
            LowWatermark = 0.3,
            ScaleDownStep = 3, // Would go to 4, but clamps to 5
            CheckInterval = TimeSpan.FromSeconds(5),
            CooldownPeriod = TimeSpan.Zero,
        });

        // Note: Initial count is 5, but we'll simulate current at 7
        var coordinator = Substitute.For<IAutoscalingCoordinator>();
        coordinator.ActiveWorkerCount.Returns(5); // Initial count for effective min calculation

        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        // Now change coordinator to return 7 current workers with low utilization
        coordinator.ActiveWorkerCount.Returns(7);
        coordinator.GetUtilizationRatio().Returns(0.1);

        // Act
        var decision = engine.EvaluateScaling();

        // Assert - Should clamp to effective min (5), not raw min (2)
        await Assert.That(decision.Action).IsEqualTo(ScalingAction.ScaleDown);
        await Assert.That(decision.TargetWorkers).IsEqualTo(5); // Clamped to effective min
    }

    private static IAutoscalingCoordinator CreateMockCoordinator(int activeWorkers, double utilization = 0.5)
    {
        var coordinator = Substitute.For<IAutoscalingCoordinator>();
        coordinator.ActiveWorkerCount.Returns(activeWorkers);
        coordinator.GetUtilizationRatio().Returns(utilization);
        return coordinator;
    }
}
