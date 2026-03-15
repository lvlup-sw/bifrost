// =============================================================================
// <copyright file="AutoscalingEngineTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling;
using Bifrost.Core.Events;

using Microsoft.Extensions.Options;

using TUnit.Core;

namespace Bifrost.Tests.Autoscaling;

/// <summary>
/// Unit tests for the <see cref="AutoscalingEngine"/> class.
/// </summary>
/// <remarks>
/// Tests cover scaling decisions based on utilization watermarks, min/max worker limits,
/// clamping behavior, cooldown period enforcement, and thread safety of concurrent evaluations.
/// </remarks>
[Property("Category", "Unit")]
public class AutoscalingEngineTests
{
    /// <summary>
    /// Verifies that high utilization above the high watermark returns a scale up decision.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges an engine with 80% high watermark, acts by evaluating with 90% utilization,
    /// asserts ScaleUp action is returned with correct worker count increase.
    /// </remarks>
    [Test]
    public async Task EvaluateScaling_HighUtilization_ReturnsScaleUp()
    {
        // Arrange
        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 1,
            MaxWorkers = 10,
            HighWatermark = 0.8,
            LowWatermark = 0.3,
            ScaleUpStep = 2,
            CooldownPeriod = TimeSpan.Zero, // No cooldown for test
        });
        var engine = new AutoscalingEngine(options);

        // Act - 90% utilization should trigger scale up
        var decision = engine.EvaluateScaling(currentWorkers: 2, utilization: 0.9, maxBacklog: 100);

        // Assert
        await Assert.That(decision.Action).IsEqualTo(ScalingAction.ScaleUp);
        await Assert.That(decision.CurrentWorkers).IsEqualTo(2);
        await Assert.That(decision.TargetWorkers).IsEqualTo(4); // 2 + 2 (step)
        await Assert.That(decision.UtilizationRatio).IsEqualTo(0.9);
    }

    /// <summary>
    /// Verifies that low utilization below the low watermark returns a scale down decision.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges an engine with 30% low watermark, acts by evaluating with 20% utilization,
    /// asserts ScaleDown action is returned with correct worker count decrease.
    /// </remarks>
    [Test]
    public async Task EvaluateScaling_LowUtilization_ReturnsScaleDown()
    {
        // Arrange
        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 1,
            MaxWorkers = 10,
            HighWatermark = 0.8,
            LowWatermark = 0.3,
            ScaleDownStep = 1,
            CooldownPeriod = TimeSpan.Zero,
        });
        var engine = new AutoscalingEngine(options);

        // Act - 20% utilization should trigger scale down
        var decision = engine.EvaluateScaling(currentWorkers: 4, utilization: 0.2, maxBacklog: 100);

        // Assert
        await Assert.That(decision.Action).IsEqualTo(ScalingAction.ScaleDown);
        await Assert.That(decision.CurrentWorkers).IsEqualTo(4);
        await Assert.That(decision.TargetWorkers).IsEqualTo(3); // 4 - 1 (step)
    }

    /// <summary>
    /// Verifies that utilization between watermarks returns no scaling action.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges an engine with 30% low and 80% high watermarks, acts by evaluating with 50% utilization,
    /// asserts None action is returned with unchanged worker count.
    /// </remarks>
    [Test]
    public async Task EvaluateScaling_NormalUtilization_ReturnsNoAction()
    {
        // Arrange
        var options = Options.Create(new AutoscalingOptions
        {
            HighWatermark = 0.8,
            LowWatermark = 0.3,
            CooldownPeriod = TimeSpan.Zero,
        });
        var engine = new AutoscalingEngine(options);

        // Act - 50% utilization is between watermarks
        var decision = engine.EvaluateScaling(currentWorkers: 4, utilization: 0.5, maxBacklog: 100);

        // Assert
        await Assert.That(decision.Action).IsEqualTo(ScalingAction.None);
        await Assert.That(decision.TargetWorkers).IsEqualTo(4); // No change
    }

    /// <summary>
    /// Verifies that scale up is blocked when already at maximum workers.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges an engine with 5 max workers already at capacity, acts by evaluating with high utilization,
    /// asserts None action is returned with a reason mentioning the max limit.
    /// </remarks>
    [Test]
    public async Task EvaluateScaling_AtMaxWorkers_DoesNotScaleUp()
    {
        // Arrange
        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 1,
            MaxWorkers = 5,
            HighWatermark = 0.8,
            ScaleUpStep = 2,
            CooldownPeriod = TimeSpan.Zero,
        });
        var engine = new AutoscalingEngine(options);

        // Act - Already at max
        var decision = engine.EvaluateScaling(currentWorkers: 5, utilization: 0.9, maxBacklog: 100);

        // Assert
        await Assert.That(decision.Action).IsEqualTo(ScalingAction.None);
        await Assert.That(decision.TargetWorkers).IsEqualTo(5);
        await Assert.That(decision.Reason).Contains("max");
    }

    /// <summary>
    /// Verifies that scale down is blocked when already at minimum workers.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges an engine with 2 min workers already at minimum, acts by evaluating with low utilization,
    /// asserts None action is returned with a reason mentioning the min limit.
    /// </remarks>
    [Test]
    public async Task EvaluateScaling_AtMinWorkers_DoesNotScaleDown()
    {
        // Arrange
        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 2,
            MaxWorkers = 10,
            LowWatermark = 0.3,
            ScaleDownStep = 1,
            CooldownPeriod = TimeSpan.Zero,
        });
        var engine = new AutoscalingEngine(options);

        // Act - Already at min
        var decision = engine.EvaluateScaling(currentWorkers: 2, utilization: 0.1, maxBacklog: 100);

        // Assert
        await Assert.That(decision.Action).IsEqualTo(ScalingAction.None);
        await Assert.That(decision.TargetWorkers).IsEqualTo(2);
        await Assert.That(decision.Reason).Contains("min");
    }

    /// <summary>
    /// Verifies that scale up clamps the target to maximum workers when step would exceed it.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges an engine where current + step exceeds max, acts by evaluating with high utilization,
    /// asserts ScaleUp action is returned with target clamped to max workers.
    /// </remarks>
    [Test]
    public async Task EvaluateScaling_ScaleUpClampsToMax()
    {
        // Arrange
        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 1,
            MaxWorkers = 5,
            HighWatermark = 0.8,
            ScaleUpStep = 3,
            CooldownPeriod = TimeSpan.Zero,
        });
        var engine = new AutoscalingEngine(options);

        // Act - 4 + 3 = 7, but max is 5
        var decision = engine.EvaluateScaling(currentWorkers: 4, utilization: 0.9, maxBacklog: 100);

        // Assert
        await Assert.That(decision.Action).IsEqualTo(ScalingAction.ScaleUp);
        await Assert.That(decision.TargetWorkers).IsEqualTo(5); // Clamped to max
    }

    /// <summary>
    /// Verifies that scale down clamps the target to minimum workers when step would go below it.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges an engine where current - step falls below min, acts by evaluating with low utilization,
    /// asserts ScaleDown action is returned with target clamped to min workers.
    /// </remarks>
    [Test]
    public async Task EvaluateScaling_ScaleDownClampsToMin()
    {
        // Arrange
        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 2,
            MaxWorkers = 10,
            LowWatermark = 0.3,
            ScaleDownStep = 3,
            CooldownPeriod = TimeSpan.Zero,
        });
        var engine = new AutoscalingEngine(options);

        // Act - 3 - 3 = 0, but min is 2
        var decision = engine.EvaluateScaling(currentWorkers: 3, utilization: 0.1, maxBacklog: 100);

        // Assert
        await Assert.That(decision.Action).IsEqualTo(ScalingAction.ScaleDown);
        await Assert.That(decision.TargetWorkers).IsEqualTo(2); // Clamped to min
    }

    /// <summary>
    /// Verifies that scaling is blocked during the cooldown period after a scaling action.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges an engine with 30-second cooldown, triggers initial scale up, then immediately evaluates again,
    /// asserts None action is returned with a reason mentioning cooldown.
    /// </remarks>
    [Test]
    public async Task EvaluateScaling_WithinCooldown_ReturnsNoAction()
    {
        // Arrange
        var options = Options.Create(new AutoscalingOptions
        {
            HighWatermark = 0.8,
            ScaleUpStep = 2,
            CooldownPeriod = TimeSpan.FromSeconds(30),
        });
        var engine = new AutoscalingEngine(options);

        // First call triggers scale up and starts cooldown
        _ = engine.EvaluateScaling(currentWorkers: 2, utilization: 0.9, maxBacklog: 100);

        // Act - Second call should be blocked by cooldown
        var decision = engine.EvaluateScaling(currentWorkers: 4, utilization: 0.9, maxBacklog: 100);

        // Assert
        await Assert.That(decision.Action).IsEqualTo(ScalingAction.None);
        await Assert.That(decision.Reason).Contains("cooldown");
    }

    /// <summary>
    /// Verifies that scaling is allowed after the cooldown period expires.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges an engine with 50ms cooldown, triggers initial scale up, waits for expiry, then evaluates again,
    /// asserts ScaleUp action is returned after cooldown elapses.
    /// </remarks>
    [Test]
    public async Task EvaluateScaling_AfterCooldown_AllowsScaling()
    {
        // Arrange
        var options = Options.Create(new AutoscalingOptions
        {
            HighWatermark = 0.8,
            ScaleUpStep = 2,
            CooldownPeriod = TimeSpan.FromMilliseconds(50),
        });
        var engine = new AutoscalingEngine(options);

        // First call triggers scale up
        _ = engine.EvaluateScaling(currentWorkers: 2, utilization: 0.9, maxBacklog: 100);

        // Wait for cooldown to expire
        await Task.Delay(100).ConfigureAwait(false);

        // Act
        var decision = engine.EvaluateScaling(currentWorkers: 4, utilization: 0.9, maxBacklog: 100);

        // Assert
        await Assert.That(decision.Action).IsEqualTo(ScalingAction.ScaleUp);
    }

    /// <summary>
    /// Verifies that scaling decisions always include a non-empty reason string.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges an engine, acts by evaluating with high utilization, asserts the decision reason is not empty.
    /// Reasons provide diagnostic context for scaling actions.
    /// </remarks>
    [Test]
    public async Task EvaluateScaling_IncludesReason()
    {
        // Arrange
        var options = Options.Create(new AutoscalingOptions
        {
            HighWatermark = 0.8,
            CooldownPeriod = TimeSpan.Zero,
        });
        var engine = new AutoscalingEngine(options);

        // Act
        var decision = engine.EvaluateScaling(currentWorkers: 2, utilization: 0.9, maxBacklog: 100);

        // Assert
        await Assert.That(decision.Reason).IsNotEmpty();
    }

    /// <summary>
    /// Verifies that ScalingDecision is a readonly record struct (value type).
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges the type via reflection, asserts it is a value type.
    /// Record structs provide value equality semantics and stack allocation.
    /// </remarks>
    [Test]
    public async Task ScalingDecision_IsReadonlyRecordStruct()
    {
        // Arrange
        var type = typeof(ScalingDecision);

        // Assert
        await Assert.That(type.IsValueType).IsTrue();
    }

    /// <summary>
    /// Verifies that concurrent calls to EvaluateScaling respect the cooldown period.
    /// Only one scaling decision should be made when multiple callers evaluate simultaneously.
    /// </summary>
    /// <remarks>
    /// This test verifies thread safety of the cooldown check. Without proper synchronization,
    /// concurrent callers could all read the same _lastScalingTime before any updates it,
    /// resulting in multiple scaling decisions bypassing the cooldown.
    /// </remarks>
    [Test]
    [Repeat(10)] // Run multiple times to increase chance of catching race condition
    public async Task EvaluateScaling_ConcurrentCalls_OnlyOneBypassesCooldown()
    {
        // Arrange
        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 1,
            MaxWorkers = 100,
            HighWatermark = 0.8,
            LowWatermark = 0.3,
            ScaleUpStep = 1,
            CooldownPeriod = TimeSpan.FromSeconds(30), // Long cooldown to ensure test validity
        });
        var engine = new AutoscalingEngine(options);

        const int concurrentCallers = 50;
        var barrier = new Barrier(concurrentCallers);
        var decisions = new ScalingDecision[concurrentCallers];

        // Act - Launch many concurrent evaluations
        var tasks = Enumerable.Range(0, concurrentCallers)
            .Select(i => Task.Run(() =>
            {
                // Synchronize all threads to start at the same time
                barrier.SignalAndWait();
                decisions[i] = engine.EvaluateScaling(currentWorkers: 2, utilization: 0.9, maxBacklog: 100);
            }))
            .ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Assert - Only one decision should have triggered a scale-up action
        var scaleUpCount = decisions.Count(d => d.Action == ScalingAction.ScaleUp);
        var cooldownCount = decisions.Count(d => d.Action == ScalingAction.None && d.Reason.Contains("cooldown", StringComparison.OrdinalIgnoreCase));

        await Assert.That(scaleUpCount).IsEqualTo(1);
        await Assert.That(cooldownCount).IsEqualTo(concurrentCallers - 1);
    }
}