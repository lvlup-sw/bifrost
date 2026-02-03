// =============================================================================
// <copyright file="ServiceCollectionExtensionsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Levelup.Channels.Core;
using Levelup.Channels.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Levelup.Channels.Tests.DependencyInjection;

/// <summary>
/// Tests for <see cref="ServiceCollectionExtensions"/>.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    /// <summary>
    /// Verifies that AddWorkOrchestrator returns a builder.
    /// </summary>
    [Test]
    public async Task AddWorkOrchestrator_ReturnsBuilder()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());

        // Act
        var builder = services.AddWorkOrchestrator<string>(opts =>
        {
            opts.Capacity = 64;
            opts.WorkerCount = 4;
        });

        // Assert
        await Assert.That(builder).IsNotNull();
        await Assert.That(builder).IsTypeOf<WorkOrchestratorBuilder<string>>();
    }

    /// <summary>
    /// Verifies that AddWorkOrchestrator throws when services is null.
    /// </summary>
    [Test]
    public async Task AddWorkOrchestrator_ThrowsWhenServicesNull()
    {
        // Arrange
        IServiceCollection services = null!;

        // Act & Assert
        await Assert.That(() => services.AddWorkOrchestrator<string>())
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that AddWorkOrchestrator registers options correctly.
    /// </summary>
    [Test]
    public async Task AddWorkOrchestrator_RegistersOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());

        // Act
        services.AddWorkOrchestrator<string>(opts =>
        {
            opts.Capacity = 256;
            opts.WorkerCount = 8;
        }).Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        // Assert
        await Assert.That(orchestrator.Capacity).IsEqualTo(256);
        await Assert.That(orchestrator.ActiveWorkers).IsEqualTo(8);

        // Cleanup
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }
}
