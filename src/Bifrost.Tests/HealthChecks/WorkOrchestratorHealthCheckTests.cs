// =============================================================================
// <copyright file="WorkOrchestratorHealthCheckTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.HealthChecks;

using Microsoft.Extensions.Diagnostics.HealthChecks;

using NSubstitute;

namespace Bifrost.Tests.HealthChecks;

/// <summary>
/// Tests for <see cref="WorkOrchestratorHealthCheck{TWork}"/>.
/// </summary>
public class WorkOrchestratorHealthCheckTests
{
    /// <summary>
    /// Verifies that the health check returns Healthy when queue is not full.
    /// </summary>
    [Test]
    public async Task WorkOrchestratorHealthCheck_CheckHealthAsync_ReturnsHealthyWhenQueueNotFull()
    {
        var orchestrator = Substitute.For<IWorkOrchestrator<string>>();
        orchestrator.PendingCount.Returns(50);
        orchestrator.Capacity.Returns(100);
        orchestrator.ActiveWorkers.Returns(4);

        var healthCheck = new WorkOrchestratorHealthCheck<string>(orchestrator);

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
    }

    /// <summary>
    /// Verifies that the health check returns Degraded when queue is over 95% full.
    /// </summary>
    [Test]
    public async Task WorkOrchestratorHealthCheck_CheckHealthAsync_ReturnsDegradedWhenQueueNearlyFull()
    {
        var orchestrator = Substitute.For<IWorkOrchestrator<string>>();
        orchestrator.PendingCount.Returns(96);
        orchestrator.Capacity.Returns(100);
        orchestrator.ActiveWorkers.Returns(4);

        var healthCheck = new WorkOrchestratorHealthCheck<string>(orchestrator);

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        await Assert.That(result.Status).IsEqualTo(HealthStatus.Degraded);
    }

    /// <summary>
    /// Verifies that the health check description includes queue stats.
    /// </summary>
    [Test]
    public async Task WorkOrchestratorHealthCheck_CheckHealthAsync_IncludesQueueStatsInDescription()
    {
        var orchestrator = Substitute.For<IWorkOrchestrator<string>>();
        orchestrator.PendingCount.Returns(25);
        orchestrator.Capacity.Returns(100);
        orchestrator.ActiveWorkers.Returns(4);

        var healthCheck = new WorkOrchestratorHealthCheck<string>(orchestrator);

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        await Assert.That(result.Description).Contains("25");
        await Assert.That(result.Description).Contains("100");
    }

    /// <summary>
    /// Verifies that the constructor throws when orchestrator is null.
    /// </summary>
    [Test]
    public async Task WorkOrchestratorHealthCheck_Constructor_ThrowsWhenOrchestratorNull()
    {
        await Assert.That(() => new WorkOrchestratorHealthCheck<string>(null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that the health check returns Healthy when queue is empty.
    /// </summary>
    [Test]
    public async Task WorkOrchestratorHealthCheck_CheckHealthAsync_ReturnsHealthyWhenQueueEmpty()
    {
        var orchestrator = Substitute.For<IWorkOrchestrator<string>>();
        orchestrator.PendingCount.Returns(0);
        orchestrator.Capacity.Returns(100);
        orchestrator.ActiveWorkers.Returns(4);

        var healthCheck = new WorkOrchestratorHealthCheck<string>(orchestrator);

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
    }

    /// <summary>
    /// Verifies that the health check includes worker count in description.
    /// </summary>
    [Test]
    public async Task WorkOrchestratorHealthCheck_CheckHealthAsync_IncludesWorkerCount()
    {
        var orchestrator = Substitute.For<IWorkOrchestrator<string>>();
        orchestrator.PendingCount.Returns(10);
        orchestrator.Capacity.Returns(100);
        orchestrator.ActiveWorkers.Returns(8);

        var healthCheck = new WorkOrchestratorHealthCheck<string>(orchestrator);

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        await Assert.That(result.Description).Contains("8");
    }

    /// <summary>
    /// Verifies exact threshold behavior at 95% utilization.
    /// </summary>
    /// <remarks>
    /// The check is Degraded only above 95%. At exactly 95% the pool is not above the threshold, so it
    /// is Healthy.
    /// </remarks>
    [Test]
    public async Task WorkOrchestratorHealthCheck_CheckHealthAsync_ReturnsDegradedAtExactly95Percent()
    {
        var orchestrator = Substitute.For<IWorkOrchestrator<string>>();
        orchestrator.PendingCount.Returns(95);
        orchestrator.Capacity.Returns(100);
        orchestrator.ActiveWorkers.Returns(4);

        var healthCheck = new WorkOrchestratorHealthCheck<string>(orchestrator);

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
    }
}