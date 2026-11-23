// =============================================================================
// <copyright file="AutoscalingEngineEffectiveMinTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling;
using Bifrost.Core.Events;

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
        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 2,
            MaxWorkers = 10,
            CheckInterval = TimeSpan.FromSeconds(5),
        });
        var coordinator = CreateMockCoordinator(activeWorkers: 2);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        await Assert.That(engine.EffectiveMinWorkers).IsEqualTo(2);
    }

    /// <summary>
    /// Verifies that EffectiveMinWorkers uses initial count when greater than config.
    /// </summary>
    /// <remarks>
    /// The initial worker count (5) is more than the configured minimum (2).
    /// </remarks>
    [Test]
    public async Task EffectiveMinWorkers_WhenInitialCountGreaterThanConfig_UsesInitialCount()
    {
        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 2,
            MaxWorkers = 10,
            CheckInterval = TimeSpan.FromSeconds(5),
        });
        var coordinator = CreateMockCoordinator(activeWorkers: 5);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        await Assert.That(engine.EffectiveMinWorkers).IsEqualTo(5);
    }

    /// <summary>
    /// Verifies that EffectiveMinWorkers uses config when initial count is less.
    /// </summary>
    /// <remarks>
    /// The initial worker count (1) is less than the configured minimum (3).
    /// </remarks>
    [Test]
    public async Task EffectiveMinWorkers_WhenInitialCountLessThanConfig_UsesConfig()
    {
        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 3,
            MaxWorkers = 10,
            CheckInterval = TimeSpan.FromSeconds(5),
        });
        var coordinator = CreateMockCoordinator(activeWorkers: 1);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        await Assert.That(engine.EffectiveMinWorkers).IsEqualTo(3);
    }

    /// <summary>
    /// Verifies that a warning is logged when there is a mismatch.
    /// </summary>
    [Test]
    public async Task Constructor_WhenMismatch_LogsWarning()
    {
        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 2,
            MaxWorkers = 10,
            CheckInterval = TimeSpan.FromSeconds(5),
        });
        var coordinator = CreateMockCoordinator(activeWorkers: 5);
        var logger = Substitute.For<ILogger<AutoscalingEngine>>();

        _ = new AutoscalingEngine(options, coordinator, logger);

        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("5") && o.ToString()!.Contains("2")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());

        var callCount = logger.ReceivedCalls().Count();
        await Assert.That(callCount).IsGreaterThan(0);
    }

    /// <summary>
    /// Verifies that no warning is logged when initial count matches config.
    /// </summary>
    /// <remarks>
    /// Only informational logs are expected, so the logger must not receive a warning.
    /// </remarks>
    [Test]
    public async Task Constructor_WhenMatch_NoWarningLogged()
    {
        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 2,
            MaxWorkers = 10,
            CheckInterval = TimeSpan.FromSeconds(5),
        });
        var coordinator = CreateMockCoordinator(activeWorkers: 2);
        var logger = Substitute.For<ILogger<AutoscalingEngine>>();

        var engine = new AutoscalingEngine(options, coordinator, logger);

        logger.DidNotReceive().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());

        await Assert.That(engine.EffectiveMinWorkers).IsEqualTo(2);
    }

    /// <summary>
    /// Verifies that scale down respects EffectiveMinWorkers.
    /// </summary>
    /// <remarks>
    /// The initial worker count (5) is more than the configured minimum (2), so the effective
    /// minimum is 5. The utilization is low, but the engine is already at the effective minimum, so
    /// it must not scale down.
    /// </remarks>
    [Test]
    public async Task EvaluateScaling_ScaleDown_RespectsEffectiveMinWorkers()
    {
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

        var decision = engine.EvaluateScaling();

        await Assert.That(decision.Action).IsEqualTo(ScalingAction.None);
        await Assert.That(decision.Reason).Contains("min");
    }

    /// <summary>
    /// Verifies that scale down targets EffectiveMinWorkers, not raw MinWorkers.
    /// </summary>
    /// <remarks>
    /// The initial worker count (5) is more than the configured minimum (2), so the effective
    /// minimum is 5. The coordinator first returns 5 for the effective-minimum calculation in the
    /// constructor. Then it returns 7 current workers with low utilization, so the engine can scale
    /// down to 5. <c>ScaleDownStep = 3</c> alone gives 4, but the decision clamps to the effective
    /// minimum (5), not to the raw minimum (2).
    /// </remarks>
    [Test]
    public async Task EvaluateScaling_ScaleDown_ClampsToEffectiveMin()
    {
        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 2,
            MaxWorkers = 10,
            HighWatermark = 0.8,
            LowWatermark = 0.3,
            ScaleDownStep = 3,
            CheckInterval = TimeSpan.FromSeconds(5),
            CooldownPeriod = TimeSpan.Zero,
        });

        var coordinator = Substitute.For<IAutoscalingCoordinator>();
        coordinator.ActiveWorkerCount.Returns(5);

        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        coordinator.ActiveWorkerCount.Returns(7);
        coordinator.GetUtilizationRatio().Returns(0.1);

        var decision = engine.EvaluateScaling();

        await Assert.That(decision.Action).IsEqualTo(ScalingAction.ScaleDown);
        await Assert.That(decision.TargetWorkers).IsEqualTo(5);
    }

    private static IAutoscalingCoordinator CreateMockCoordinator(int activeWorkers, double utilization = 0.5)
    {
        var coordinator = Substitute.For<IAutoscalingCoordinator>();
        coordinator.ActiveWorkerCount.Returns(activeWorkers);
        coordinator.GetUtilizationRatio().Returns(utilization);
        return coordinator;
    }
}