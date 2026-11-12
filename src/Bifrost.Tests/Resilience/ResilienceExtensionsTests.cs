// =============================================================================
// <copyright file="ResilienceExtensionsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost;
using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.DependencyInjection;
using Bifrost.Resilience;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TUnit.Core;

namespace Bifrost.Tests.Resilience;

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
        var options = provider.GetService<IOptionsMonitor<ResiliencySettings>>();
        await Assert.That(options).IsNotNull();
        await Assert.That(options!.Get(WorkOptions.Name<string>())).IsNotNull();
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
        var options = provider.GetRequiredService<IOptionsMonitor<ResiliencySettings>>().Get(WorkOptions.Name<string>());
        await Assert.That(options.RetryCount).IsEqualTo(5);
        await Assert.That(options.TimeoutIntervalSeconds).IsEqualTo(30);
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
        var options = provider.GetRequiredService<IOptionsMonitor<ResiliencySettings>>().Get(WorkOptions.Name<string>());
        await Assert.That(options.RetryCount).IsEqualTo(3);
        await Assert.That(options.UseExponentialBackoff).IsTrue();
        await Assert.That(options.TimeoutIntervalSeconds).IsEqualTo(5);
        await Assert.That(options.CircuitBreakerCount).IsEqualTo(3);
    }

    /// <summary>
    /// Two work types keep their own retry counts. A later pipeline must not replace an earlier one.
    /// </summary>
    [Test]
    public async Task WithResilience_DifferentWorkTypes_KeepIndependentRetryCounts()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IWorkHandler<LeftWork>, NoopWorkHandler<LeftWork>>();
        services.AddSingleton<IWorkHandler<RightWork>, NoopWorkHandler<RightWork>>();
        services.AddSingleton<ILogger<WorkOrchestrator<LeftWork>>>(NullLogger<WorkOrchestrator<LeftWork>>.Instance);
        services.AddSingleton<ILogger<WorkOrchestrator<RightWork>>>(NullLogger<WorkOrchestrator<RightWork>>.Instance);

        services.AddWorkOrchestrator<LeftWork>(options => options.Capacity = 4)
            .WithResilience(settings => settings.RetryCount = 0)
            .WithDeadLetterQueue(options => options.MaxRetries = 0)
            .Build();
        services.AddWorkOrchestrator<RightWork>(options =>
            {
                options.Capacity = 9;
                options.DispatchStrategy = DispatchStrategy.PriorityExact;
                options.FullMode = QueueFullMode.Reject;
            })
            .WithResilience(settings => settings.RetryCount = 5)
            .WithDeadLetterQueue(options => options.MaxRetries = 2)
            .Build();

        await using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IWorkOrchestrator<LeftWork>>();
        _ = provider.GetRequiredService<IWorkOrchestrator<RightWork>>();

        var resilience = provider.GetRequiredService<IOptionsMonitor<ResiliencySettings>>();
        var orchestrators = provider.GetRequiredService<IOptionsMonitor<WorkOrchestratorOptions>>();
        var deadLetters = provider.GetRequiredService<IOptionsMonitor<DeadLetterQueueOptions>>();

        await Assert.That(resilience.Get(WorkOptions.Name<LeftWork>()).RetryCount).IsEqualTo(0);
        await Assert.That(resilience.Get(WorkOptions.Name<RightWork>()).RetryCount).IsEqualTo(5);
        await Assert.That(orchestrators.Get(WorkOptions.Name<LeftWork>()).Capacity).IsEqualTo(4);
        await Assert.That(orchestrators.Get(WorkOptions.Name<LeftWork>()).DispatchStrategy).IsEqualTo(DispatchStrategy.Fifo);
        await Assert.That(orchestrators.Get(WorkOptions.Name<RightWork>()).Capacity).IsEqualTo(9);
        await Assert.That(orchestrators.Get(WorkOptions.Name<RightWork>()).DispatchStrategy).IsEqualTo(DispatchStrategy.PriorityExact);
        await Assert.That(deadLetters.Get(WorkOptions.Name<LeftWork>()).MaxRetries).IsEqualTo(0);
        await Assert.That(deadLetters.Get(WorkOptions.Name<RightWork>()).MaxRetries).IsEqualTo(2);
    }

    private sealed class LeftWork;

    private sealed class RightWork;

    private sealed class NoopWorkHandler<TWork> : IWorkHandler<TWork>
    {
        public ValueTask HandleAsync(TWork work, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}