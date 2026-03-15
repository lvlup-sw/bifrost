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
        builder.HandlerDecorators.Add(new HandlerDecoratorRegistration<string>(
            Order: 0,
            Factory: (sp, handler) =>
            {
                decoratorApplied = true;
                return handler;
            }));

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
        builder.HandlerDecorators.Add(new HandlerDecoratorRegistration<string>(
            Order: 0,
            Factory: (sp, handler) =>
            {
                order.Add(1);
                return handler;
            }));
        builder.HandlerDecorators.Add(new HandlerDecoratorRegistration<string>(
            Order: 1,
            Factory: (sp, handler) =>
            {
                order.Add(2);
                return handler;
            }));

        builder.Build();

        var provider = services.BuildServiceProvider();

        // Act
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        // Assert
        await Assert.That(order).HasCount(2);
        await Assert.That(order[0]).IsEqualTo(1);
        await Assert.That(order[1]).IsEqualTo(2);

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

    /// <summary>
    /// Verifies that handler decorators with explicit Order values are applied in order
    /// (lowest Order = innermost, applied first).
    /// </summary>
    [Test]
    public async Task Build_WithOrderedHandlerDecorators_AppliesInOrder()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var innerHandler = Substitute.For<IWorkHandler<string>>();
        services.AddSingleton(innerHandler);

        var builder = services.AddWorkOrchestrator<string>();

        var applicationOrder = new List<int>();

        // Add Order=2 first, then Order=1 — they should be applied in order 1, 2
        builder.HandlerDecorators.Add(new HandlerDecoratorRegistration<string>(
            Order: 2,
            Factory: (sp, handler) =>
            {
                applicationOrder.Add(2);
                return handler;
            }));
        builder.HandlerDecorators.Add(new HandlerDecoratorRegistration<string>(
            Order: 1,
            Factory: (sp, handler) =>
            {
                applicationOrder.Add(1);
                return handler;
            }));

        builder.Build();

        var provider = services.BuildServiceProvider();

        // Act
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        // Assert — Order=1 should be applied first (innermost), then Order=2
        await Assert.That(applicationOrder).HasCount(2);
        await Assert.That(applicationOrder[0]).IsEqualTo(1);
        await Assert.That(applicationOrder[1]).IsEqualTo(2);

        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that Build throws when two handler decorators have the same Order value.
    /// </summary>
    [Test]
    public async Task Build_WithDuplicateHandlerDecoratorOrder_ThrowsInvalidOperationException()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        var builder = services.AddWorkOrchestrator<string>();

        builder.HandlerDecorators.Add(new HandlerDecoratorRegistration<string>(
            Order: 0,
            Factory: (sp, handler) => handler));
        builder.HandlerDecorators.Add(new HandlerDecoratorRegistration<string>(
            Order: 0,
            Factory: (sp, handler) => handler));

        // Act & Assert
        await Assert.That(() => builder.Build())
            .Throws<InvalidOperationException>();
    }
}
