// =============================================================================
// <copyright file="WorkerRegistryHealthCheckTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling;
using Bifrost.HealthChecks;

using Microsoft.Extensions.Diagnostics.HealthChecks;

using NSubstitute;

namespace Bifrost.Tests.HealthChecks;

/// <summary>
/// Tests for <see cref="WorkerRegistryHealthCheck"/>.
/// </summary>
public class WorkerRegistryHealthCheckTests
{
    /// <summary>
    /// Verifies that the constructor throws when registry is null.
    /// </summary>
    [Test]
    public async Task WorkerRegistryHealthCheck_Constructor_ThrowsWhenRegistryNull()
    {
        await Assert.That(() => new WorkerRegistryHealthCheck(null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that the health check returns Unhealthy when there are no active workers.
    /// </summary>
    [Test]
    public async Task WorkerRegistryHealthCheck_CheckHealthAsync_ReturnsUnhealthyWhenNoWorkers()
    {
        var registry = Substitute.For<IWorkerRegistry>();
        registry.ActiveWorkerCount.Returns(0);
        registry.IdleWorkerCount.Returns(0);
        registry.GetAllWorkers().Returns([]);

        var healthCheck = new WorkerRegistryHealthCheck(registry);

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
    }

    /// <summary>
    /// Verifies that the health check returns Degraded when all workers are busy.
    /// </summary>
    [Test]
    public async Task WorkerRegistryHealthCheck_CheckHealthAsync_ReturnsDegradedWhenAllWorkersBusy()
    {
        var registry = Substitute.For<IWorkerRegistry>();
        registry.ActiveWorkerCount.Returns(4);
        registry.IdleWorkerCount.Returns(0);
        registry.GetAllWorkers().Returns(CreateWorkerInfos(4, 0, 0));

        var healthCheck = new WorkerRegistryHealthCheck(registry);

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        await Assert.That(result.Status).IsEqualTo(HealthStatus.Degraded);
    }

    /// <summary>
    /// Verifies that the health check returns Healthy when workers have capacity.
    /// </summary>
    [Test]
    public async Task WorkerRegistryHealthCheck_CheckHealthAsync_ReturnsHealthyWhenWorkersHaveCapacity()
    {
        var registry = Substitute.For<IWorkerRegistry>();
        registry.ActiveWorkerCount.Returns(4);
        registry.IdleWorkerCount.Returns(2);
        registry.GetAllWorkers().Returns(CreateWorkerInfos(4, 2, 0));

        var healthCheck = new WorkerRegistryHealthCheck(registry);

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
    }

    /// <summary>
    /// Verifies that the health check includes worker counts in data.
    /// </summary>
    [Test]
    public async Task WorkerRegistryHealthCheck_CheckHealthAsync_IncludesWorkerCountsInData()
    {
        var registry = Substitute.For<IWorkerRegistry>();
        registry.ActiveWorkerCount.Returns(4);
        registry.IdleWorkerCount.Returns(2);
        registry.GetAllWorkers().Returns(CreateWorkerInfos(4, 2, 0));

        var healthCheck = new WorkerRegistryHealthCheck(registry);

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        await Assert.That(result.Data).IsNotNull();
        await Assert.That(result.Data!.ContainsKey("ActiveWorkers")).IsTrue();
        await Assert.That(result.Data!.ContainsKey("IdleWorkers")).IsTrue();
        await Assert.That(result.Data!.ContainsKey("BusyWorkers")).IsTrue();
        await Assert.That(result.Data!.ContainsKey("StoppingWorkers")).IsTrue();
    }

    /// <summary>
    /// Verifies that the health check returns correct worker counts in data.
    /// </summary>
    /// <remarks>
    /// Five active workers with two idle gives three busy workers. <c>StoppingWorkers</c> is 0, because
    /// a test cannot set <c>StopRequested</c> (it is internal).
    /// </remarks>
    [Test]
    public async Task WorkerRegistryHealthCheck_CheckHealthAsync_ReturnsCorrectWorkerCounts()
    {
        var registry = Substitute.For<IWorkerRegistry>();
        registry.ActiveWorkerCount.Returns(5);
        registry.IdleWorkerCount.Returns(2);
        registry.GetAllWorkers().Returns(CreateWorkerInfos(5, 2, 0));

        var healthCheck = new WorkerRegistryHealthCheck(registry);

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        await Assert.That(result.Data!["ActiveWorkers"]).IsEqualTo(5);
        await Assert.That(result.Data!["IdleWorkers"]).IsEqualTo(2);
        await Assert.That(result.Data!["BusyWorkers"]).IsEqualTo(3);
        await Assert.That(result.Data!["StoppingWorkers"]).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies that the description describes the health state.
    /// </summary>
    [Test]
    public async Task WorkerRegistryHealthCheck_CheckHealthAsync_IncludesDescriptiveMessage()
    {
        var registry = Substitute.For<IWorkerRegistry>();
        registry.ActiveWorkerCount.Returns(0);
        registry.IdleWorkerCount.Returns(0);
        registry.GetAllWorkers().Returns([]);

        var healthCheck = new WorkerRegistryHealthCheck(registry);

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        await Assert.That(result.Description).IsNotNull();
        await Assert.That(result.Description!.Length).IsGreaterThan(0);
    }

    // The first `idle` workers get IsIdle = true. WorkerInfo.RequestStop() is internal, so this helper
    // cannot mark the last `stopping` workers as stopping. The stopping branch is empty on purpose.
    private static IReadOnlyCollection<WorkerInfo> CreateWorkerInfos(
        int total,
        int idle,
        int stopping)
    {
        var workers = new List<WorkerInfo>();
        for (int i = 0; i < total; i++)
        {
            var worker = new WorkerInfo($"worker-{i}");

            if (i < idle)
            {
                worker.IsIdle = true;
            }

            if (i >= total - stopping)
            {
            }

            workers.Add(worker);
        }

        return workers.AsReadOnly();
    }
}