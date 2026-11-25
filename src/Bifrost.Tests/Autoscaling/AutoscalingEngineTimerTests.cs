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
        var options = CreateOptions();
        var coordinator = CreateMockCoordinator(activeWorkers: 2);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        await Assert.That(engine.IsRunning).IsFalse();
    }

    /// <summary>
    /// Verifies that StartAsync sets IsRunning to true.
    /// </summary>
    [Test]
    public async Task StartAsync_WhenNotRunning_SetsIsRunningTrue()
    {
        var options = CreateOptions();
        var coordinator = CreateMockCoordinator(activeWorkers: 2);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        await engine.StartAsync();

        await Assert.That(engine.IsRunning).IsTrue();

        await engine.StopAsync();
    }

    /// <summary>
    /// Verifies that StartAsync is idempotent.
    /// </summary>
    /// <remarks>
    /// The second <c>StartAsync</c> call must be idempotent.
    /// </remarks>
    [Test]
    public async Task StartAsync_WhenAlreadyRunning_RemainsRunning()
    {
        var options = CreateOptions();
        var coordinator = CreateMockCoordinator(activeWorkers: 2);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        await engine.StartAsync();
        await engine.StartAsync();

        await Assert.That(engine.IsRunning).IsTrue();

        await engine.StopAsync();
    }

    /// <summary>
    /// Verifies that StopAsync sets IsRunning to false.
    /// </summary>
    [Test]
    public async Task StopAsync_WhenRunning_SetsIsRunningFalse()
    {
        var options = CreateOptions();
        var coordinator = CreateMockCoordinator(activeWorkers: 2);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);
        await engine.StartAsync();

        await engine.StopAsync();

        await Assert.That(engine.IsRunning).IsFalse();
    }

    /// <summary>
    /// Verifies that StopAsync is idempotent.
    /// </summary>
    /// <remarks>
    /// The <c>StopAsync</c> call must not throw.
    /// </remarks>
    [Test]
    public async Task StopAsync_WhenNotRunning_RemainsNotRunning()
    {
        var options = CreateOptions();
        var coordinator = CreateMockCoordinator(activeWorkers: 2);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        await engine.StopAsync();

        await Assert.That(engine.IsRunning).IsFalse();
    }

    /// <summary>
    /// Verifies that the timer fires at the configured interval.
    /// </summary>
    /// <remarks>
    /// The test waits for the events deterministically, because the thread pool can be slow under
    /// CI load. It must receive at least 2 evaluations.
    /// </remarks>
    [Test]
    public async Task StartAsync_WithCheckInterval_FiresEvaluationPeriodically()
    {
        var options = CreateOptions(checkInterval: TimeSpan.FromMilliseconds(50));
        var coordinator = CreateMockCoordinator(activeWorkers: 2, utilization: 0.5);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        var eventCount = 0;
        var twoEventsReceived = new TaskCompletionSource();
        engine.ScalingDecisionMade += (_, _) =>
        {
            if (Interlocked.Increment(ref eventCount) >= 2)
            {
                twoEventsReceived.TrySetResult();
            }
        };

        await engine.StartAsync();

        var completed = await Task.WhenAny(
            twoEventsReceived.Task,
            Task.Delay(TimeSpan.FromSeconds(10)));

        await engine.StopAsync();

        await Assert.That(ReferenceEquals(completed, twoEventsReceived.Task)).IsTrue();
        await Assert.That(eventCount).IsGreaterThanOrEqualTo(2);
    }

    /// <summary>
    /// Verifies that stopping the engine stops the timer.
    /// </summary>
    /// <remarks>
    /// The test waits for at least one event before it stops the engine. The 300 ms delay after the
    /// stop gives a late evaluation time to occur. The event count must not change after the stop.
    /// </remarks>
    [Test]
    public async Task StopAsync_StopsTimerEvaluation()
    {
        var options = CreateOptions(checkInterval: TimeSpan.FromMilliseconds(50));
        var coordinator = CreateMockCoordinator(activeWorkers: 2, utilization: 0.5);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

        var eventCount = 0;
        var firstEventReceived = new TaskCompletionSource();
        engine.ScalingDecisionMade += (_, _) =>
        {
            Interlocked.Increment(ref eventCount);
            firstEventReceived.TrySetResult();
        };

        await engine.StartAsync();

        await firstEventReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await engine.StopAsync();
        var countAfterStop = eventCount;
        await Task.Delay(300);

        await Assert.That(eventCount).IsEqualTo(countAfterStop);
    }

    /// <summary>
    /// Verifies that the Configuration property returns the options.
    /// </summary>
    [Test]
    public async Task Configuration_ReturnsOptions()
    {
        var options = CreateOptions();
        var coordinator = CreateMockCoordinator(activeWorkers: 2);
        var engine = new AutoscalingEngine(options, coordinator, NullLogger<AutoscalingEngine>.Instance);

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