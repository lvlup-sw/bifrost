// =============================================================================
// <copyright file="ResilienceExtensionsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Levelup.Channels.Core;
using Levelup.Channels.DependencyInjection;
using Levelup.Channels.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TUnit.Core;

namespace Levelup.Channels.Tests.Resilience;

/// <summary>
/// Tests for <see cref="ResilienceExtensions"/> extension methods.
/// </summary>
[Property("Category", "Unit")]
public class ResilienceExtensionsTests
{
    /// <summary>
    /// Verifies WithResilience throws on null builder.
    /// </summary>
    [Test]
    public async Task WithResilience_NullBuilder_Throws()
    {
        // Act & Assert
        await Assert.That(() => ResilienceExtensions.WithResilience<string>(null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies WithResilience returns builder for chaining.
    /// </summary>
    [Test]
    public async Task WithResilience_ValidBuilder_ReturnsBuilder()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IWorkHandler<string>>(Substitute.For<IWorkHandler<string>>());
        services.AddSingleton<ILogger<WorkOrchestrator<string>>>(NullLogger<WorkOrchestrator<string>>.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        var builder = services.AddWorkOrchestrator<string>();

        // Act
        var result = builder.WithResilience();

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result).IsEqualTo(builder);
    }

    /// <summary>
    /// Verifies WithResilience registers ResiliencySettings options.
    /// </summary>
    [Test]
    public async Task WithResilience_RegistersResiliencySettingsOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IWorkHandler<string>>(Substitute.For<IWorkHandler<string>>());
        services.AddSingleton<ILogger<WorkOrchestrator<string>>>(NullLogger<WorkOrchestrator<string>>.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        var builder = services.AddWorkOrchestrator<string>();

        // Act
        builder.WithResilience();
        builder.Build();
        var provider = services.BuildServiceProvider();

        // Assert
        var options = provider.GetService<IOptions<ResiliencySettings>>();
        await Assert.That(options).IsNotNull();
        await Assert.That(options!.Value).IsNotNull();
    }

    /// <summary>
    /// Verifies WithResilience applies custom configuration.
    /// </summary>
    [Test]
    public async Task WithResilience_CustomConfiguration_AppliesSettings()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IWorkHandler<string>>(Substitute.For<IWorkHandler<string>>());
        services.AddSingleton<ILogger<WorkOrchestrator<string>>>(NullLogger<WorkOrchestrator<string>>.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        var builder = services.AddWorkOrchestrator<string>();

        // Act
        builder.WithResilience(options =>
        {
            options.RetryCount = 5;
            options.TimeoutIntervalSeconds = 30;
        });
        builder.Build();
        var provider = services.BuildServiceProvider();

        // Assert
        var options = provider.GetRequiredService<IOptions<ResiliencySettings>>();
        await Assert.That(options.Value.RetryCount).IsEqualTo(5);
        await Assert.That(options.Value.TimeoutIntervalSeconds).IsEqualTo(30);
    }

    /// <summary>
    /// Verifies WithResilience adds decorator to orchestrator.
    /// </summary>
    [Test]
    public async Task WithResilience_AddsDecoratorToOrchestrator()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IWorkHandler<string>>(Substitute.For<IWorkHandler<string>>());
        services.AddSingleton<ILogger<WorkOrchestrator<string>>>(NullLogger<WorkOrchestrator<string>>.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        var builder = services.AddWorkOrchestrator<string>();

        // Act
        builder.WithResilience();
        builder.Build();
        var provider = services.BuildServiceProvider();

        // Assert
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        await Assert.That(orchestrator).IsAssignableTo<ResilientOrchestrator<string>>();
    }

    /// <summary>
    /// Verifies resilience decorator is inner to autoscaling decorator.
    /// </summary>
    [Test]
    public async Task WithResilience_AndAutoscaling_ResilienceIsInner()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IWorkHandler<string>>(Substitute.For<IWorkHandler<string>>());
        services.AddSingleton<ILogger<WorkOrchestrator<string>>>(NullLogger<WorkOrchestrator<string>>.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        var builder = services.AddWorkOrchestrator<string>();

        // Act - order should not matter, decorator order values should determine final wrapping
        builder.WithResilience();
        builder.WithAutoscaling();
        builder.Build();
        var provider = services.BuildServiceProvider();

        // Assert - autoscaling should be outermost (order 100), resilience should be inner (order 50)
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        // The outermost decorator should be AutoscalingOrchestrator
        // since autoscaling has order 100 and resilience has order 50
        await Assert.That(orchestrator.GetType().Name).Contains("Autoscaling");
    }

    /// <summary>
    /// Verifies WithResilience default settings have expected values.
    /// </summary>
    [Test]
    public async Task WithResilience_DefaultSettings_HaveExpectedValues()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IWorkHandler<string>>(Substitute.For<IWorkHandler<string>>());
        services.AddSingleton<ILogger<WorkOrchestrator<string>>>(NullLogger<WorkOrchestrator<string>>.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        var builder = services.AddWorkOrchestrator<string>();

        // Act
        builder.WithResilience();
        builder.Build();
        var provider = services.BuildServiceProvider();

        // Assert
        var options = provider.GetRequiredService<IOptions<ResiliencySettings>>();
        await Assert.That(options.Value.RetryCount).IsEqualTo(3);
        await Assert.That(options.Value.UseExponentialBackoff).IsTrue();
        await Assert.That(options.Value.TimeoutIntervalSeconds).IsEqualTo(5);
        await Assert.That(options.Value.CircuitBreakerCount).IsEqualTo(3);
    }
}
