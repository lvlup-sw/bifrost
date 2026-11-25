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
using Microsoft.Extensions.Options;

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
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());

        var builder = services.AddWorkOrchestrator<string>();
        builder.WithHealthChecks();
        builder.Build();

        var provider = services.BuildServiceProvider();

        var orchestrator = provider.GetService<IWorkOrchestrator<string>>();
        await Assert.That(orchestrator).IsNotNull();

        var healthCheckService = provider.GetService<HealthCheckService>();
        await Assert.That(healthCheckService).IsNotNull();

        var report = await healthCheckService!.CheckHealthAsync();
        await Assert.That(report.Entries).IsNotEmpty();

        if (orchestrator is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies that WithHealthChecks uses custom name when provided.
    /// </summary>
    [Test]
    public async Task WithHealthChecks_UsesCustomName()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());

        var builder = services.AddWorkOrchestrator<string>();
        builder.WithHealthChecks("custom-health-check");
        builder.Build();

        var provider = services.BuildServiceProvider();

        var healthCheckService = provider.GetService<HealthCheckService>();
        await Assert.That(healthCheckService).IsNotNull();

        var report = await healthCheckService!.CheckHealthAsync();
        var hasCustomName = report.Entries.ContainsKey("custom-health-check");
        await Assert.That(hasCustomName).IsTrue();

        var orchestrator = provider.GetService<IWorkOrchestrator<string>>();
        if (orchestrator is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies that WithHealthChecks returns the builder for chaining.
    /// </summary>
    [Test]
    public async Task WithHealthChecks_ReturnsBuilderForChaining()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());

        var builder = services.AddWorkOrchestrator<string>();

        var result = builder.WithHealthChecks();

        await Assert.That(result).IsEqualTo(builder);
    }

    /// <summary>
    /// Verifies that AddWorkOrchestratorHealthCheck registers the health check.
    /// </summary>
    [Test]
    public async Task AddWorkOrchestratorHealthCheck_RegistersHealthCheck()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());

        var builder = services.AddWorkOrchestrator<string>();
        builder.Build();

        services.AddWorkOrchestratorHealthCheck<string>();

        var provider = services.BuildServiceProvider();
        var healthCheckService = provider.GetService<HealthCheckService>();

        await Assert.That(healthCheckService).IsNotNull();

        var orchestrator = provider.GetService<IWorkOrchestrator<string>>();
        if (orchestrator is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies that AddAutoscalingHealthChecks registers the autoscaling health check.
    /// </summary>
    [Test]
    public async Task AddAutoscalingHealthChecks_RegistersAutoscalingHealthCheck()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var mockRegistry = Substitute.For<IWorkerRegistry>();
        mockRegistry.ActiveWorkerCount.Returns(4);
        mockRegistry.IdleWorkerCount.Returns(2);
        mockRegistry.GetAllWorkers().Returns([]);

        services.AddKeyedSingleton<IWorkerRegistry>(typeof(string), mockRegistry);
        services.AddKeyedSingleton<IOptions<AutoscalingOptions>>(typeof(string),
            Options.Create(new AutoscalingOptions { MinWorkers = 1, MaxWorkers = 16 }));

        services.AddHealthChecks()
            .AddAutoscalingHealthChecks<string>();

        var provider = services.BuildServiceProvider();
        var healthCheckService = provider.GetService<HealthCheckService>();

        await Assert.That(healthCheckService).IsNotNull();

        var report = await healthCheckService!.CheckHealthAsync();
        await Assert.That(report.Entries.ContainsKey("autoscaling-engine-System.String")).IsTrue();
    }

    /// <summary>
    /// Verifies that AddAutoscalingHealthChecks registers the worker registry health check.
    /// </summary>
    [Test]
    public async Task AddAutoscalingHealthChecks_RegistersWorkerRegistryHealthCheck()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var mockRegistry = Substitute.For<IWorkerRegistry>();
        mockRegistry.ActiveWorkerCount.Returns(4);
        mockRegistry.IdleWorkerCount.Returns(2);
        mockRegistry.GetAllWorkers().Returns([]);

        services.AddKeyedSingleton<IWorkerRegistry>(typeof(string), mockRegistry);
        services.AddKeyedSingleton<IOptions<AutoscalingOptions>>(typeof(string),
            Options.Create(new AutoscalingOptions { MinWorkers = 1, MaxWorkers = 16 }));

        services.AddHealthChecks()
            .AddAutoscalingHealthChecks<string>();

        var provider = services.BuildServiceProvider();
        var healthCheckService = provider.GetService<HealthCheckService>();

        await Assert.That(healthCheckService).IsNotNull();

        var report = await healthCheckService!.CheckHealthAsync();
        await Assert.That(report.Entries.ContainsKey("worker-registry-System.String")).IsTrue();
    }

    /// <summary>
    /// Verifies that AddAutoscalingHealthChecks returns the builder for chaining.
    /// </summary>
    [Test]
    public async Task AddAutoscalingHealthChecks_ReturnsBuilderForChaining()
    {
        var services = new ServiceCollection();
        var builder = services.AddHealthChecks();

        var result = builder.AddAutoscalingHealthChecks<string>();

        await Assert.That(result).IsEqualTo(builder);
    }

    /// <summary>
    /// Verifies that AddAutoscalingHealthChecks throws when builder is null.
    /// </summary>
    [Test]
    public async Task AddAutoscalingHealthChecks_ThrowsWhenBuilderNull()
    {
        await Assert.That(() => HealthCheckExtensions.AddAutoscalingHealthChecks<string>(null!))
            .Throws<ArgumentNullException>();
    }
}
