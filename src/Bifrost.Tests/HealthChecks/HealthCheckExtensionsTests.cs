// =============================================================================
// <copyright file="HealthCheckExtensionsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling;
using Bifrost.Core;
using Bifrost.DependencyInjection;
using Bifrost.HealthChecks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using NSubstitute;

namespace Bifrost.Tests.HealthChecks;

/// <summary>
/// Tests for <see cref="HealthCheckExtensions"/>.
/// </summary>
public class HealthCheckExtensionsTests
{
    /// <summary>
    /// Verifies that WithHealthChecks registers the health check.
    /// </summary>
    [Test]
    public async Task WithHealthChecks_RegistersHealthCheck()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());

        var builder = services.AddWorkOrchestrator<string>();
        builder.WithHealthChecks();
        builder.Build();

        // Act
        var provider = services.BuildServiceProvider();

        // Get the orchestrator to ensure it was built
        var orchestrator = provider.GetService<IWorkOrchestrator<string>>();
        await Assert.That(orchestrator).IsNotNull();

        // Get the health check service to verify the health check is registered
        var healthCheckService = provider.GetService<HealthCheckService>();
        await Assert.That(healthCheckService).IsNotNull();

        // Verify by running the health check
        var report = await healthCheckService!.CheckHealthAsync().ConfigureAwait(false);
        await Assert.That(report.Entries).IsNotEmpty();

        // Cleanup
        if (orchestrator is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies that WithHealthChecks uses custom name when provided.
    /// </summary>
    [Test]
    public async Task WithHealthChecks_UsesCustomName()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());

        var builder = services.AddWorkOrchestrator<string>();
        builder.WithHealthChecks("custom-health-check");
        builder.Build();

        var provider = services.BuildServiceProvider();

        // Get health check service
        var healthCheckService = provider.GetService<HealthCheckService>();
        await Assert.That(healthCheckService).IsNotNull();

        // Verify custom name is in the report
        var report = await healthCheckService!.CheckHealthAsync().ConfigureAwait(false);
        var hasCustomName = report.Entries.ContainsKey("custom-health-check");
        await Assert.That(hasCustomName).IsTrue();

        // Cleanup
        var orchestrator = provider.GetService<IWorkOrchestrator<string>>();
        if (orchestrator is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies that WithHealthChecks returns the builder for chaining.
    /// </summary>
    [Test]
    public async Task WithHealthChecks_ReturnsBuilderForChaining()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());

        var builder = services.AddWorkOrchestrator<string>();

        // Act
        var result = builder.WithHealthChecks();

        // Assert
        await Assert.That(result).IsEqualTo(builder);
    }

    /// <summary>
    /// Verifies that AddWorkOrchestratorHealthCheck registers the health check.
    /// </summary>
    [Test]
    public async Task AddWorkOrchestratorHealthCheck_RegistersHealthCheck()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());

        var builder = services.AddWorkOrchestrator<string>();
        builder.Build();

        // Act - Use the separate extension method
        services.AddWorkOrchestratorHealthCheck<string>();

        var provider = services.BuildServiceProvider();
        var healthCheckService = provider.GetService<HealthCheckService>();

        // Assert
        await Assert.That(healthCheckService).IsNotNull();

        // Cleanup
        var orchestrator = provider.GetService<IWorkOrchestrator<string>>();
        if (orchestrator is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies that AddAutoscalingHealthChecks registers the autoscaling health check.
    /// </summary>
    [Test]
    public async Task AddAutoscalingHealthChecks_RegistersAutoscalingHealthCheck()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var mockRegistry = Substitute.For<IWorkerRegistry>();
        mockRegistry.ActiveWorkerCount.Returns(4);
        mockRegistry.IdleWorkerCount.Returns(2);
        mockRegistry.GetAllWorkers().Returns([]);

        services.AddSingleton(mockRegistry);
        services.Configure<AutoscalingOptions>(options =>
        {
            options.MinWorkers = 1;
            options.MaxWorkers = 16;
        });

        // Act
        services.AddHealthChecks()
            .AddAutoscalingHealthChecks();

        var provider = services.BuildServiceProvider();
        var healthCheckService = provider.GetService<HealthCheckService>();

        // Assert
        await Assert.That(healthCheckService).IsNotNull();

        var report = await healthCheckService!.CheckHealthAsync().ConfigureAwait(false);
        await Assert.That(report.Entries.ContainsKey("autoscaling-engine")).IsTrue();
    }

    /// <summary>
    /// Verifies that AddAutoscalingHealthChecks registers the worker registry health check.
    /// </summary>
    [Test]
    public async Task AddAutoscalingHealthChecks_RegistersWorkerRegistryHealthCheck()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var mockRegistry = Substitute.For<IWorkerRegistry>();
        mockRegistry.ActiveWorkerCount.Returns(4);
        mockRegistry.IdleWorkerCount.Returns(2);
        mockRegistry.GetAllWorkers().Returns([]);

        services.AddSingleton(mockRegistry);
        services.Configure<AutoscalingOptions>(options =>
        {
            options.MinWorkers = 1;
            options.MaxWorkers = 16;
        });

        // Act
        services.AddHealthChecks()
            .AddAutoscalingHealthChecks();

        var provider = services.BuildServiceProvider();
        var healthCheckService = provider.GetService<HealthCheckService>();

        // Assert
        await Assert.That(healthCheckService).IsNotNull();

        var report = await healthCheckService!.CheckHealthAsync().ConfigureAwait(false);
        await Assert.That(report.Entries.ContainsKey("worker-registry")).IsTrue();
    }

    /// <summary>
    /// Verifies that AddAutoscalingHealthChecks returns the builder for chaining.
    /// </summary>
    [Test]
    public async Task AddAutoscalingHealthChecks_ReturnsBuilderForChaining()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = services.AddHealthChecks();

        // Act
        var result = builder.AddAutoscalingHealthChecks();

        // Assert
        await Assert.That(result).IsEqualTo(builder);
    }

    /// <summary>
    /// Verifies that AddAutoscalingHealthChecks throws when builder is null.
    /// </summary>
    [Test]
    public async Task AddAutoscalingHealthChecks_ThrowsWhenBuilderNull()
    {
        // Act & Assert
        await Assert.That(() => HealthCheckExtensions.AddAutoscalingHealthChecks(null!))
            .Throws<ArgumentNullException>();
    }
}