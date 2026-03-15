// =============================================================================
// <copyright file="WorkOrchestratorBuilderTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using TUnit.Core;

namespace Bifrost.Tests.DependencyInjection;

/// <summary>
/// Tests for <see cref="WorkOrchestratorBuilder{TWork}"/>.
/// </summary>
[Property("Category", "Unit")]
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

    /// <summary>
    /// Verifies that Build throws when two orchestrator decorators have the same Order value.
    /// </summary>
    [Test]
    public async Task Build_WithDuplicateDecoratorOrder_ThrowsInvalidOperationException()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        var builder = services.AddWorkOrchestrator<string>();

        builder.Decorators.Add(new DecoratorRegistration<string>(
            Order: 1,
            Factory: (sp, inner) => inner));
        builder.Decorators.Add(new DecoratorRegistration<string>(
            Order: 1,
            Factory: (sp, inner) => inner));

        // Act & Assert
        await Assert.That(() => builder.Build())
            .Throws<InvalidOperationException>();
    }

    /// <summary>
    /// Verifies that Build succeeds when all orchestrator decorators have unique Order values.
    /// </summary>
    [Test]
    public async Task Build_WithUniqueDecoratorOrders_Succeeds()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        var builder = services.AddWorkOrchestrator<string>();

        builder.Decorators.Add(new DecoratorRegistration<string>(
            Order: 1,
            Factory: (sp, inner) => inner));
        builder.Decorators.Add(new DecoratorRegistration<string>(
            Order: 2,
            Factory: (sp, inner) => inner));

        // Act & Assert - should not throw
        await Assert.That(() => builder.Build()).ThrowsNothing();
    }
}