// =============================================================================
// <copyright file="AutoscalingHealthCheckTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling;
using Bifrost.HealthChecks;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

using NSubstitute;

namespace Bifrost.Tests.HealthChecks;

/// <summary>
/// Tests for <see cref="AutoscalingHealthCheck"/>.
/// </summary>
public class AutoscalingHealthCheckTests
{
    /// <summary>
    /// Verifies that the constructor throws when registry is null.
    /// </summary>
    [Test]
    public async Task AutoscalingHealthCheck_Constructor_ThrowsWhenRegistryNull()
    {
        var options = Options.Create(new AutoscalingOptions());

        await Assert.That(() => new AutoscalingHealthCheck(null!, options))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that the constructor throws when options is null.
    /// </summary>
    [Test]
    public async Task AutoscalingHealthCheck_Constructor_ThrowsWhenOptionsNull()
    {
        var registry = Substitute.For<IWorkerRegistry>();

        await Assert.That(() => new AutoscalingHealthCheck(registry, null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that the health check returns Unhealthy when no workers are active.
    /// </summary>
    [Test]
    public async Task AutoscalingHealthCheck_CheckHealthAsync_ReturnsUnhealthyWhenNoWorkers()
    {
        var registry = Substitute.For<IWorkerRegistry>();
        registry.ActiveWorkerCount.Returns(0);
        registry.IdleWorkerCount.Returns(0);
        registry.GetAllWorkers().Returns([]);

        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 1,
            MaxWorkers = 16,
        });

        var healthCheck = new AutoscalingHealthCheck(registry, options);

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
    }

    /// <summary>
    /// Verifies that the health check returns Degraded when at max workers.
    /// </summary>
    [Test]
    public async Task AutoscalingHealthCheck_CheckHealthAsync_ReturnsDegradedAtMaxWorkers()
    {
        var registry = Substitute.For<IWorkerRegistry>();
        registry.ActiveWorkerCount.Returns(16);
        registry.IdleWorkerCount.Returns(0);
        registry.GetAllWorkers().Returns(CreateWorkerInfos(16));

        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 1,
            MaxWorkers = 16,
        });

        var healthCheck = new AutoscalingHealthCheck(registry, options);

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        await Assert.That(result.Status).IsEqualTo(HealthStatus.Degraded);
    }

    /// <summary>
    /// Verifies that the health check returns Degraded when at min workers with all busy.
    /// </summary>
    [Test]
    public async Task AutoscalingHealthCheck_CheckHealthAsync_ReturnsDegradedAtMinWorkersAllBusy()
    {
        var registry = Substitute.For<IWorkerRegistry>();
        registry.ActiveWorkerCount.Returns(1);
        registry.IdleWorkerCount.Returns(0);
        registry.GetAllWorkers().Returns(CreateWorkerInfos(1));

        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 1,
            MaxWorkers = 16,
        });

        var healthCheck = new AutoscalingHealthCheck(registry, options);

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        await Assert.That(result.Status).IsEqualTo(HealthStatus.Degraded);
    }

    /// <summary>
    /// Verifies that the health check returns Healthy when workers are within normal range.
    /// </summary>
    [Test]
    public async Task AutoscalingHealthCheck_CheckHealthAsync_ReturnsHealthyWhenWithinNormalRange()
    {
        var registry = Substitute.For<IWorkerRegistry>();
        registry.ActiveWorkerCount.Returns(4);
        registry.IdleWorkerCount.Returns(2);
        registry.GetAllWorkers().Returns(CreateWorkerInfos(4, 2));

        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 1,
            MaxWorkers = 16,
        });

        var healthCheck = new AutoscalingHealthCheck(registry, options);

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
    }

    /// <summary>
    /// Verifies that the health check includes autoscaling data in result.
    /// </summary>
    [Test]
    public async Task AutoscalingHealthCheck_CheckHealthAsync_IncludesAutoscalingDataInResult()
    {
        var registry = Substitute.For<IWorkerRegistry>();
        registry.ActiveWorkerCount.Returns(4);
        registry.IdleWorkerCount.Returns(2);
        registry.GetAllWorkers().Returns(CreateWorkerInfos(4, 2));

        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 2,
            MaxWorkers = 10,
        });

        var healthCheck = new AutoscalingHealthCheck(registry, options);

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        await Assert.That(result.Data).IsNotNull();
        await Assert.That(result.Data!.ContainsKey("ActiveWorkers")).IsTrue();
        await Assert.That(result.Data!.ContainsKey("MinWorkers")).IsTrue();
        await Assert.That(result.Data!.ContainsKey("MaxWorkers")).IsTrue();
    }

    /// <summary>
    /// Verifies that the health check returns correct data values.
    /// </summary>
    [Test]
    public async Task AutoscalingHealthCheck_CheckHealthAsync_ReturnsCorrectDataValues()
    {
        var registry = Substitute.For<IWorkerRegistry>();
        registry.ActiveWorkerCount.Returns(8);
        registry.IdleWorkerCount.Returns(3);
        registry.GetAllWorkers().Returns(CreateWorkerInfos(8, 3));

        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 2,
            MaxWorkers = 10,
        });

        var healthCheck = new AutoscalingHealthCheck(registry, options);

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        await Assert.That(result.Data!["ActiveWorkers"]).IsEqualTo(8);
        await Assert.That(result.Data!["MinWorkers"]).IsEqualTo(2);
        await Assert.That(result.Data!["MaxWorkers"]).IsEqualTo(10);
    }

    /// <summary>
    /// Verifies that the health check description contains meaningful information.
    /// </summary>
    [Test]
    public async Task AutoscalingHealthCheck_CheckHealthAsync_IncludesDescriptiveMessage()
    {
        var registry = Substitute.For<IWorkerRegistry>();
        registry.ActiveWorkerCount.Returns(4);
        registry.IdleWorkerCount.Returns(2);
        registry.GetAllWorkers().Returns(CreateWorkerInfos(4, 2));

        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 1,
            MaxWorkers = 16,
        });

        var healthCheck = new AutoscalingHealthCheck(registry, options);

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        await Assert.That(result.Description).IsNotNull();
        await Assert.That(result.Description!.Length).IsGreaterThan(0);
    }

    /// <summary>
    /// Verifies that the health check handles at min workers with some idle as Healthy.
    /// </summary>
    /// <remarks>
    /// The pool is at min workers, but some workers are idle, so it has capacity and is Healthy.
    /// </remarks>
    [Test]
    public async Task AutoscalingHealthCheck_CheckHealthAsync_ReturnsHealthyAtMinWorkersWithIdle()
    {
        var registry = Substitute.For<IWorkerRegistry>();
        registry.ActiveWorkerCount.Returns(2);
        registry.IdleWorkerCount.Returns(1);
        registry.GetAllWorkers().Returns(CreateWorkerInfos(2, 1));

        var options = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 2,
            MaxWorkers = 16,
        });

        var healthCheck = new AutoscalingHealthCheck(registry, options);

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
    }

    private static IReadOnlyCollection<WorkerInfo> CreateWorkerInfos(int total, int idle = 0)
    {
        var workers = new List<WorkerInfo>();
        for (int i = 0; i < total; i++)
        {
            var worker = new WorkerInfo($"worker-{i}")
            {
                IsIdle = i < idle,
            };
            workers.Add(worker);
        }

        return workers.AsReadOnly();
    }
}