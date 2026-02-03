// =============================================================================
// <copyright file="AutoscalingExtensionsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling;
using Bifrost.Core;
using Bifrost.Decorators;
using Bifrost.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using TUnit.Core;

namespace Bifrost.Tests.DependencyInjection;

/// <summary>
/// Tests for <see cref="AutoscalingExtensions"/> builder extensions.
/// </summary>
[Property("Category", "Unit")]
public class AutoscalingExtensionsTests
{
    /// <summary>
    /// Verifies WithAutoscaling adds decorator.
    /// </summary>
    [Test]
    public async Task WithAutoscaling_AddsDecorator()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        services.AddLogging();

        // Act
        var builder = services.AddWorkOrchestrator<string>()
            .WithAutoscaling();
        builder.Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        // Assert - The outer orchestrator should be the AutoscalingOrchestrator
        await Assert.That(orchestrator).IsAssignableTo<AutoscalingOrchestrator<string>>();
    }

    /// <summary>
    /// Verifies WithAutoscaling registers metrics.
    /// </summary>
    [Test]
    public async Task WithAutoscaling_RegistersMetrics()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        services.AddLogging();

        // Act
        var builder = services.AddWorkOrchestrator<string>()
            .WithAutoscaling();
        builder.Build();

        var provider = services.BuildServiceProvider();
        var metrics = provider.GetService<IWorkerMetrics>();

        // Assert
        await Assert.That(metrics).IsNotNull();
        await Assert.That(metrics).IsAssignableTo<WorkerMetrics>();
    }

    /// <summary>
    /// Verifies WithAutoscaling registers options.
    /// </summary>
    [Test]
    public async Task WithAutoscaling_RegistersOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        services.AddLogging();

        // Act
        var builder = services.AddWorkOrchestrator<string>()
            .WithAutoscaling(opts =>
            {
                opts.MinWorkers = 3;
                opts.MaxWorkers = 20;
            });
        builder.Build();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AutoscalingOptions>>();

        // Assert
        await Assert.That(options.Value.MinWorkers).IsEqualTo(3);
        await Assert.That(options.Value.MaxWorkers).IsEqualTo(20);
    }

    /// <summary>
    /// Verifies WithAutoscaling returns same builder for chaining.
    /// </summary>
    [Test]
    public async Task WithAutoscaling_ReturnsSameBuilder()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        services.AddLogging();

        // Act
        var builder = services.AddWorkOrchestrator<string>();
        var result = builder.WithAutoscaling();

        // Assert
        await Assert.That(result).IsEqualTo(builder);
    }

    /// <summary>
    /// Verifies WithAutoscaling registers AutoscalingEngine.
    /// </summary>
    [Test]
    public async Task WithAutoscaling_RegistersEngine()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        services.AddLogging();

        // Act
        var builder = services.AddWorkOrchestrator<string>()
            .WithAutoscaling();
        builder.Build();

        var provider = services.BuildServiceProvider();
        var engine = provider.GetService<AutoscalingEngine>();

        // Assert
        await Assert.That(engine).IsNotNull();
    }

    /// <summary>
    /// Verifies WithAutoscaling with null configure uses defaults.
    /// </summary>
    [Test]
    public async Task WithAutoscaling_NullConfigure_UsesDefaults()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        services.AddLogging();

        // Act
        var builder = services.AddWorkOrchestrator<string>()
            .WithAutoscaling(null);
        builder.Build();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AutoscalingOptions>>();

        // Assert - Should use default values
        await Assert.That(options.Value.MinWorkers).IsEqualTo(1);
        await Assert.That(options.Value.MaxWorkers).IsEqualTo(16);
    }
}
