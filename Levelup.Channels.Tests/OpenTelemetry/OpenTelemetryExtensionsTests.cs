// =============================================================================
// <copyright file="OpenTelemetryExtensionsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Levelup.Channels.Core;
using Levelup.Channels.DependencyInjection;
using Levelup.Channels.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TUnit.Core;

namespace Levelup.Channels.Tests.OpenTelemetry;

/// <summary>
/// Tests for <see cref="OpenTelemetryExtensions"/>.
/// </summary>
[Property("Category", "Unit")]
public class OpenTelemetryExtensionsTests
{
    /// <summary>
    /// Verifies that WithOpenTelemetry throws when builder is null.
    /// </summary>
    [Test]
    public async Task WithOpenTelemetry_NullBuilder_Throws()
    {
        // Act & Assert
        await Assert.That(() => OpenTelemetryExtensions.WithOpenTelemetry<string>(null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that WithOpenTelemetry returns the builder for chaining.
    /// </summary>
    [Test]
    public async Task WithOpenTelemetry_ValidBuilder_ReturnsBuilder()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IWorkHandler<string>>(Substitute.For<IWorkHandler<string>>());
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        var builder = services.AddWorkOrchestrator<string>();

        // Act
        var result = builder.WithOpenTelemetry();

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result).IsEqualTo(builder);
    }

    /// <summary>
    /// Verifies that WithOpenTelemetry registers OrchestratorMetrics.
    /// </summary>
    [Test]
    public async Task WithOpenTelemetry_RegistersMetrics()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IWorkHandler<string>>(Substitute.For<IWorkHandler<string>>());
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        var builder = services.AddWorkOrchestrator<string>();

        // Act
        builder.WithOpenTelemetry();
        builder.Build();
        var provider = services.BuildServiceProvider();

        // Assert
        var metrics = provider.GetService<OrchestratorMetrics<string>>();
        await Assert.That(metrics).IsNotNull();

        // Cleanup
        var orchestrator = provider.GetService<IWorkOrchestrator<string>>();
        if (orchestrator is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }

        metrics?.Dispose();
    }

    /// <summary>
    /// Verifies that multiple calls to WithOpenTelemetry do not duplicate registration.
    /// </summary>
    [Test]
    public async Task WithOpenTelemetry_MultipleCalls_DoesNotDuplicate()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IWorkHandler<string>>(Substitute.For<IWorkHandler<string>>());
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        var builder = services.AddWorkOrchestrator<string>();

        // Act
        builder.WithOpenTelemetry();
        builder.WithOpenTelemetry();
        builder.Build();
        var provider = services.BuildServiceProvider();

        // Assert - should resolve the same singleton instance
        var metrics1 = provider.GetService<OrchestratorMetrics<string>>();
        var metrics2 = provider.GetService<OrchestratorMetrics<string>>();
        await Assert.That(metrics1).IsNotNull();
        await Assert.That(ReferenceEquals(metrics1, metrics2)).IsTrue();

        // Cleanup
        var orchestrator = provider.GetService<IWorkOrchestrator<string>>();
        if (orchestrator is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }

        metrics1?.Dispose();
    }

    /// <summary>
    /// Verifies that WithOpenTelemetry works with other extensions.
    /// </summary>
    [Test]
    public async Task WithOpenTelemetry_CombinesWithOtherExtensions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IWorkHandler<string>>(Substitute.For<IWorkHandler<string>>());
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        var builder = services.AddWorkOrchestrator<string>();

        // Act
        builder
            .WithOpenTelemetry()
            .WithAutoscaling();
        builder.Build();
        var provider = services.BuildServiceProvider();

        // Assert
        var metrics = provider.GetService<OrchestratorMetrics<string>>();
        var orchestrator = provider.GetService<IWorkOrchestrator<string>>();
        await Assert.That(metrics).IsNotNull();
        await Assert.That(orchestrator).IsNotNull();

        // Cleanup
        if (orchestrator is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }

        metrics?.Dispose();
    }
}
