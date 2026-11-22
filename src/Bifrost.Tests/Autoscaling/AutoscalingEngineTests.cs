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
    /// <para><c>CooldownPeriod</c> is zero, so no cooldown applies. The 90% utilization triggers a
    /// scale up to 2 + 2 (the step) = 4.</para>
    /// </remarks>
    [Test]
    public async Task EvaluateScaling_HighUtilization_ReturnsScaleUp()
    {
        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 1,
            MaxWorkers = 10,
            HighWatermark = 0.8,
            LowWatermark = 0.3,
            ScaleUpStep = 2,
            CooldownPeriod = TimeSpan.Zero,
        });
        var engine = new AutoscalingEngine(options);

        var decision = engine.EvaluateScaling(currentWorkers: 2, utilization: 0.9, maxBacklog: 100);

        await Assert.That(decision.Action).IsEqualTo(ScalingAction.ScaleUp);
        await Assert.That(decision.CurrentWorkers).IsEqualTo(2);
        await Assert.That(decision.TargetWorkers).IsEqualTo(4);
        await Assert.That(decision.UtilizationRatio).IsEqualTo(0.9);
    }

    /// <summary>
    /// Verifies that low utilization below the low watermark returns a scale down decision.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges an engine with 30% low watermark, acts by evaluating with 20% utilization,
    /// asserts ScaleDown action is returned with correct worker count decrease.
    /// <para>The 20% utilization triggers a scale down to 4 - 1 (the step) = 3.</para>
    /// </remarks>
    [Test]
    public async Task EvaluateScaling_LowUtilization_ReturnsScaleDown()
    {
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

        var decision = engine.EvaluateScaling(currentWorkers: 4, utilization: 0.2, maxBacklog: 100);

        await Assert.That(decision.Action).IsEqualTo(ScalingAction.ScaleDown);
        await Assert.That(decision.CurrentWorkers).IsEqualTo(4);
        await Assert.That(decision.TargetWorkers).IsEqualTo(3);
    }

    /// <summary>
    /// Verifies that utilization between watermarks returns no scaling action.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges an engine with 30% low and 80% high watermarks, acts by evaluating with 50% utilization,
    /// asserts None action is returned with unchanged worker count.
    /// <para>The 50% utilization is between the watermarks, so the worker count does not
    /// change.</para>
    /// </remarks>
    [Test]
    public async Task EvaluateScaling_NormalUtilization_ReturnsNoAction()
    {
        var options = Options.Create(new AutoscalingOptions
        {
            HighWatermark = 0.8,
            LowWatermark = 0.3,
            CooldownPeriod = TimeSpan.Zero,
        });
        var engine = new AutoscalingEngine(options);

        var decision = engine.EvaluateScaling(currentWorkers: 4, utilization: 0.5, maxBacklog: 100);

        await Assert.That(decision.Action).IsEqualTo(ScalingAction.None);
        await Assert.That(decision.TargetWorkers).IsEqualTo(4);
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
        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 1,
            MaxWorkers = 5,
            HighWatermark = 0.8,
            ScaleUpStep = 2,
            CooldownPeriod = TimeSpan.Zero,
        });
        var engine = new AutoscalingEngine(options);

        var decision = engine.EvaluateScaling(currentWorkers: 5, utilization: 0.9, maxBacklog: 100);

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
        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 2,
            MaxWorkers = 10,
            LowWatermark = 0.3,
            ScaleDownStep = 1,
            CooldownPeriod = TimeSpan.Zero,
        });
        var engine = new AutoscalingEngine(options);

        var decision = engine.EvaluateScaling(currentWorkers: 2, utilization: 0.1, maxBacklog: 100);

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
    /// <para>4 + 3 = 7, but the maximum is 5, so the target clamps to 5.</para>
    /// </remarks>
    [Test]
    public async Task EvaluateScaling_ScaleUpClampsToMax()
    {
        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 1,
            MaxWorkers = 5,
            HighWatermark = 0.8,
            ScaleUpStep = 3,
            CooldownPeriod = TimeSpan.Zero,
        });
        var engine = new AutoscalingEngine(options);

        var decision = engine.EvaluateScaling(currentWorkers: 4, utilization: 0.9, maxBacklog: 100);

        await Assert.That(decision.Action).IsEqualTo(ScalingAction.ScaleUp);
        await Assert.That(decision.TargetWorkers).IsEqualTo(5);
    }

    /// <summary>
    /// Verifies that scale down clamps the target to minimum workers when step would go below it.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges an engine where current - step falls below min, acts by evaluating with low utilization,
    /// asserts ScaleDown action is returned with target clamped to min workers.
    /// <para>3 - 3 = 0, but the minimum is 2, so the target clamps to 2.</para>
    /// </remarks>
    [Test]
    public async Task EvaluateScaling_ScaleDownClampsToMin()
    {
        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 2,
            MaxWorkers = 10,
            LowWatermark = 0.3,
            ScaleDownStep = 3,
            CooldownPeriod = TimeSpan.Zero,
        });
        var engine = new AutoscalingEngine(options);

        var decision = engine.EvaluateScaling(currentWorkers: 3, utilization: 0.1, maxBacklog: 100);

        await Assert.That(decision.Action).IsEqualTo(ScalingAction.ScaleDown);
        await Assert.That(decision.TargetWorkers).IsEqualTo(2);
    }

    /// <summary>
    /// Verifies that scaling is blocked during the cooldown period after a scaling action.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges an engine with 30-second cooldown, triggers initial scale up, then immediately evaluates again,
    /// asserts None action is returned with a reason mentioning cooldown.
    /// <para>The first call triggers a scale up and starts the cooldown. The cooldown blocks the
    /// second call.</para>
    /// </remarks>
    [Test]
    public async Task EvaluateScaling_WithinCooldown_ReturnsNoAction()
    {
        var options = Options.Create(new AutoscalingOptions
        {
            HighWatermark = 0.8,
            ScaleUpStep = 2,
            CooldownPeriod = TimeSpan.FromSeconds(30),
        });
        var engine = new AutoscalingEngine(options);

        _ = engine.EvaluateScaling(currentWorkers: 2, utilization: 0.9, maxBacklog: 100);

        var decision = engine.EvaluateScaling(currentWorkers: 4, utilization: 0.9, maxBacklog: 100);

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
    /// <para>The first call triggers a scale up. The test then waits for the cooldown to
    /// expire.</para>
    /// </remarks>
    [Test]
    public async Task EvaluateScaling_AfterCooldown_AllowsScaling()
    {
        var options = Options.Create(new AutoscalingOptions
        {
            HighWatermark = 0.8,
            ScaleUpStep = 2,
            CooldownPeriod = TimeSpan.FromMilliseconds(50),
        });
        var engine = new AutoscalingEngine(options);

        _ = engine.EvaluateScaling(currentWorkers: 2, utilization: 0.9, maxBacklog: 100);

        await Task.Delay(100).ConfigureAwait(false);

        var decision = engine.EvaluateScaling(currentWorkers: 4, utilization: 0.9, maxBacklog: 100);

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
        var options = Options.Create(new AutoscalingOptions
        {
            HighWatermark = 0.8,
            CooldownPeriod = TimeSpan.Zero,
        });
        var engine = new AutoscalingEngine(options);

        var decision = engine.EvaluateScaling(currentWorkers: 2, utilization: 0.9, maxBacklog: 100);

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
        var type = typeof(ScalingDecision);

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
    /// <para><c>[Repeat(10)]</c> runs the test many times to increase the chance to catch the race.
    /// The 30 s <c>CooldownPeriod</c> is long so that the test stays valid. The callers run on
    /// dedicated threads (<c>TaskCreationOptions.LongRunning</c>), so the synchronous
    /// <c>Barrier</c> waits cannot starve the shared thread pool on 4-vCPU runners.
    /// <c>barrier.SignalAndWait</c> makes all threads start at the same time. Only one decision can
    /// trigger a scale-up action.</para>
    /// </remarks>
    [Test]
    [Repeat(10)]
    [NotInParallel(nameof(EvaluateScaling_ConcurrentCalls_OnlyOneBypassesCooldown))]
    public async Task EvaluateScaling_ConcurrentCalls_OnlyOneBypassesCooldown()
    {
        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 1,
            MaxWorkers = 100,
            HighWatermark = 0.8,
            LowWatermark = 0.3,
            ScaleUpStep = 1,
            CooldownPeriod = TimeSpan.FromSeconds(30),
        });
        using var engine = new AutoscalingEngine(options);

        const int concurrentCallers = 50;
        using var barrier = new Barrier(concurrentCallers);
        var decisions = new ScalingDecision[concurrentCallers];

        var tasks = Enumerable.Range(0, concurrentCallers)
            .Select(i => Task.Factory.StartNew(
                () =>
                {
                    barrier.SignalAndWait();
                    decisions[i] = engine.EvaluateScaling(currentWorkers: 2, utilization: 0.9, maxBacklog: 100);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var scaleUpCount = decisions.Count(d => d.Action == ScalingAction.ScaleUp);
        var cooldownCount = decisions.Count(d => d.Action == ScalingAction.None && d.Reason.Contains("cooldown", StringComparison.OrdinalIgnoreCase));

        await Assert.That(scaleUpCount).IsEqualTo(1);
        await Assert.That(cooldownCount).IsEqualTo(concurrentCallers - 1);
    }
}