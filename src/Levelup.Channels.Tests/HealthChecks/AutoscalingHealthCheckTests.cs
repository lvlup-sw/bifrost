// =============================================================================
// <copyright file="AutoscalingHealthCheckTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Levelup.Channels.Autoscaling;
using Levelup.Channels.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Levelup.Channels.Tests.HealthChecks;

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
        // Arrange
        var options = Options.Create(new AutoscalingOptions());

        // Act & Assert
        await Assert.That(() => new AutoscalingHealthCheck(null!, options))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that the constructor throws when options is null.
    /// </summary>
    [Test]
    public async Task AutoscalingHealthCheck_Constructor_ThrowsWhenOptionsNull()
    {
        // Arrange
        var registry = Substitute.For<IWorkerRegistry>();

        // Act & Assert
        await Assert.That(() => new AutoscalingHealthCheck(registry, null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that the health check returns Unhealthy when no workers are active.
    /// </summary>
    [Test]
    public async Task AutoscalingHealthCheck_CheckHealthAsync_ReturnsUnhealthyWhenNoWorkers()
    {
        // Arrange
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

        // Act
        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        // Assert
        await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
    }

    /// <summary>
    /// Verifies that the health check returns Degraded when at max workers.
    /// </summary>
    [Test]
    public async Task AutoscalingHealthCheck_CheckHealthAsync_ReturnsDegradedAtMaxWorkers()
    {
        // Arrange
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

        // Act
        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        // Assert
        await Assert.That(result.Status).IsEqualTo(HealthStatus.Degraded);
    }

    /// <summary>
    /// Verifies that the health check returns Degraded when at min workers with all busy.
    /// </summary>
    [Test]
    public async Task AutoscalingHealthCheck_CheckHealthAsync_ReturnsDegradedAtMinWorkersAllBusy()
    {
        // Arrange
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

        // Act
        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        // Assert
        await Assert.That(result.Status).IsEqualTo(HealthStatus.Degraded);
    }

    /// <summary>
    /// Verifies that the health check returns Healthy when workers are within normal range.
    /// </summary>
    [Test]
    public async Task AutoscalingHealthCheck_CheckHealthAsync_ReturnsHealthyWhenWithinNormalRange()
    {
        // Arrange
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

        // Act
        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        // Assert
        await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
    }

    /// <summary>
    /// Verifies that the health check includes autoscaling data in result.
    /// </summary>
    [Test]
    public async Task AutoscalingHealthCheck_CheckHealthAsync_IncludesAutoscalingDataInResult()
    {
        // Arrange
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

        // Act
        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        // Assert
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
        // Arrange
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

        // Act
        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        // Assert
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
        // Arrange
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

        // Act
        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        // Assert
        await Assert.That(result.Description).IsNotNull();
        await Assert.That(result.Description!.Length).IsGreaterThan(0);
    }

    /// <summary>
    /// Verifies that the health check handles at min workers with some idle as Healthy.
    /// </summary>
    [Test]
    public async Task AutoscalingHealthCheck_CheckHealthAsync_ReturnsHealthyAtMinWorkersWithIdle()
    {
        // Arrange
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

        // Act
        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None).ConfigureAwait(false);

        // Assert - at min workers but with some idle is Healthy (has capacity)
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
