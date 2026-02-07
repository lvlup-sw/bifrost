// =============================================================================
// <copyright file="WorkOrchestratorBuilderTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

namespace Bifrost.Tests.DependencyInjection;

/// <summary>
/// Tests for <see cref="WorkOrchestratorBuilder{TWork}"/>.
/// </summary>
public class WorkOrchestratorBuilderTests
{
    /// <summary>
    /// Verifies that Build registers the orchestrator in the service collection.
    /// </summary>
    [Test]
    public async Task WorkOrchestratorBuilder_Build_RegistersOrchestrator()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        var builder = services.AddWorkOrchestrator<string>();

        // Act
        builder.Build();
        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetService<IWorkOrchestrator<string>>();

        // Assert
        await Assert.That(orchestrator).IsNotNull();

        // Cleanup
        if (orchestrator is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies that Build applies custom configuration options.
    /// </summary>
    [Test]
    public async Task WorkOrchestratorBuilder_Build_AppliesOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        var builder = services.AddWorkOrchestrator<string>(opts =>
        {
            opts.Capacity = 64;
            opts.WorkerCount = 4;
        });

        // Act
        builder.Build();
        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetService<IWorkOrchestrator<string>>();

        // Assert
        await Assert.That(orchestrator).IsNotNull();
        await Assert.That(orchestrator!.Capacity).IsEqualTo(64);
        await Assert.That(orchestrator!.ActiveWorkers).IsEqualTo(4);

        // Cleanup
        if (orchestrator is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }
}