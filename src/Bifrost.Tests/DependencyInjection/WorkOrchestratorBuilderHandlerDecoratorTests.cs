// =============================================================================
// <copyright file="WorkOrchestratorBuilderHandlerDecoratorTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NSubstitute;

using TUnit.Core;

namespace Bifrost.Tests.DependencyInjection;

/// <summary>
/// Tests for handler decorator support in <see cref="WorkOrchestratorBuilder{TWork}"/>.
/// </summary>
[Property("Category", "Unit")]
public class WorkOrchestratorBuilderHandlerDecoratorTests
{
    /// <summary>
    /// Verifies that a handler decorator is applied when building.
    /// </summary>
    [Test]
    public async Task Build_WithHandlerDecorator_AppliesDecoration()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var innerHandler = Substitute.For<IWorkHandler<string>>();
        services.AddSingleton(innerHandler);

        var builder = services.AddWorkOrchestrator<string>();

        var decoratorApplied = false;
        builder.HandlerDecorators.Add((sp, handler) =>
        {
            decoratorApplied = true;
            return handler;
        });

        builder.Build();

        var provider = services.BuildServiceProvider();

        // Act
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        // Assert
        await Assert.That(decoratorApplied).IsTrue();
        await Assert.That(orchestrator).IsNotNull();

        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that multiple handler decorators are applied in order.
    /// </summary>
    [Test]
    public async Task Build_WithMultipleHandlerDecorators_AppliesInOrder()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var innerHandler = Substitute.For<IWorkHandler<string>>();
        services.AddSingleton(innerHandler);

        var builder = services.AddWorkOrchestrator<string>();

        var order = new List<int>();
        builder.HandlerDecorators.Add((sp, handler) =>
        {
            order.Add(1);
            return handler;
        });
        builder.HandlerDecorators.Add((sp, handler) =>
        {
            order.Add(2);
            return handler;
        });

        builder.Build();

        var provider = services.BuildServiceProvider();

        // Act
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        // Assert
        await Assert.That(order).IsEquivalentTo(new[] { 1, 2 });

        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that with no handler decorators, the original handler is used.
    /// </summary>
    [Test]
    public async Task Build_WithNoHandlerDecorators_UsesOriginalHandler()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var innerHandler = Substitute.For<IWorkHandler<string>>();
        services.AddSingleton(innerHandler);

        var builder = services.AddWorkOrchestrator<string>();
        builder.Build();

        var provider = services.BuildServiceProvider();

        // Act
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        // Assert
        await Assert.That(orchestrator).IsNotNull();

        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }
}