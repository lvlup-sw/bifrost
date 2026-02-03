// =============================================================================
// <copyright file="AutoscalingEngineTimerTests.cs" company="Levelup Software">
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
/// Tests for <see cref="AutoscalingEngine"/> timer-based periodic evaluation.
/// </summary>
[Property("Category", "Unit")]
public class AutoscalingEngineTimerTests
{
    /// <summary>
    /// Verifies that the engine is not running initially.
    /// </summary>
    [Test]
    public async Task IsRunning_Initially_ReturnsFalse()
    {
        // Arrange
        var options = CreateOptions();
        var coordinator = CreateMockCoordinator(activeWorkers: 2);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        // Assert
        await Assert.That(engine.IsRunning).IsFalse();
    }

    /// <summary>
    /// Verifies that StartAsync sets IsRunning to true.
    /// </summary>
    [Test]
    public async Task StartAsync_WhenNotRunning_SetsIsRunningTrue()
    {
        // Arrange
        var options = CreateOptions();
        var coordinator = CreateMockCoordinator(activeWorkers: 2);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        // Act
        await engine.StartAsync().ConfigureAwait(false);

        // Assert
        await Assert.That(engine.IsRunning).IsTrue();

        // Cleanup
        await engine.StopAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that StartAsync is idempotent.
    /// </summary>
    [Test]
    public async Task StartAsync_WhenAlreadyRunning_RemainsRunning()
    {
        // Arrange
        var options = CreateOptions();
        var coordinator = CreateMockCoordinator(activeWorkers: 2);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        // Act
        await engine.StartAsync().ConfigureAwait(false);
        await engine.StartAsync().ConfigureAwait(false); // Second call should be idempotent

        // Assert
        await Assert.That(engine.IsRunning).IsTrue();

        // Cleanup
        await engine.StopAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that StopAsync sets IsRunning to false.
    /// </summary>
    [Test]
    public async Task StopAsync_WhenRunning_SetsIsRunningFalse()
    {
        // Arrange
        var options = CreateOptions();
        var coordinator = CreateMockCoordinator(activeWorkers: 2);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);
        await engine.StartAsync().ConfigureAwait(false);

        // Act
        await engine.StopAsync().ConfigureAwait(false);

        // Assert
        await Assert.That(engine.IsRunning).IsFalse();
    }

    /// <summary>
    /// Verifies that StopAsync is idempotent.
    /// </summary>
    [Test]
    public async Task StopAsync_WhenNotRunning_RemainsNotRunning()
    {
        // Arrange
        var options = CreateOptions();
        var coordinator = CreateMockCoordinator(activeWorkers: 2);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        // Act
        await engine.StopAsync().ConfigureAwait(false); // Should not throw

        // Assert
        await Assert.That(engine.IsRunning).IsFalse();
    }

    /// <summary>
    /// Verifies that the timer fires at the configured interval.
    /// </summary>
    [Test]
    public async Task StartAsync_WithCheckInterval_FiresEvaluationPeriodically()
    {
        // Arrange
        var options = CreateOptions(checkInterval: TimeSpan.FromMilliseconds(50));
        var coordinator = CreateMockCoordinator(activeWorkers: 2, utilization: 0.5);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        var eventCount = 0;
        engine.ScalingDecisionMade += (_, _) => Interlocked.Increment(ref eventCount);

        // Act
        await engine.StartAsync().ConfigureAwait(false);
        await Task.Delay(300).ConfigureAwait(false); // Should fire at least 5 times (50ms interval)
        await engine.StopAsync().ConfigureAwait(false);

        // Assert - Should have at least 2 evaluations (accounting for timing variance and CI load)
        await Assert.That(eventCount).IsGreaterThanOrEqualTo(2);
    }

    /// <summary>
    /// Verifies that stopping the engine stops the timer.
    /// </summary>
    [Test]
    public async Task StopAsync_StopsTimerEvaluation()
    {
        // Arrange
        var options = CreateOptions(checkInterval: TimeSpan.FromMilliseconds(50));
        var coordinator = CreateMockCoordinator(activeWorkers: 2, utilization: 0.5);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        var eventCount = 0;
        engine.ScalingDecisionMade += (_, _) => Interlocked.Increment(ref eventCount);

        // Act
        await engine.StartAsync().ConfigureAwait(false);
        await Task.Delay(100).ConfigureAwait(false); // Let some evaluations happen
        await engine.StopAsync().ConfigureAwait(false);
        var countAfterStop = eventCount;
        await Task.Delay(150).ConfigureAwait(false); // Wait to ensure no more evaluations

        // Assert - No new events after stop
        await Assert.That(eventCount).IsEqualTo(countAfterStop);
    }

    /// <summary>
    /// Verifies that the Configuration property returns the options.
    /// </summary>
    [Test]
    public async Task Configuration_ReturnsOptions()
    {
        // Arrange
        var options = CreateOptions();
        var coordinator = CreateMockCoordinator(activeWorkers: 2);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        // Assert
        await Assert.That(engine.Configuration).IsNotNull();
        await Assert.That(engine.Configuration.MinWorkers).IsEqualTo(1);
        await Assert.That(engine.Configuration.MaxWorkers).IsEqualTo(10);
    }

    private static IOptions<AutoscalingOptions> CreateOptions(
        TimeSpan? checkInterval = null,
        TimeSpan? cooldownPeriod = null)
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
            CooldownPeriod = cooldownPeriod ?? TimeSpan.Zero,
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
