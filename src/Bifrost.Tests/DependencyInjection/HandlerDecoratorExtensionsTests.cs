// =============================================================================
// <copyright file="HandlerDecoratorExtensionsTests.cs" company="Levelup Software">
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
/// Tests for <see cref="HandlerDecoratorExtensions"/>.
/// </summary>
[Property("Category", "Unit")]
public class HandlerDecoratorExtensionsTests
{
    /// <summary>
    /// Verifies that WithHandlerDecorator registers a decorator in the builder's HandlerDecorators list.
    /// </summary>
    [Test]
    public async Task WithHandlerDecorator_RegistersDecorator()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        var builder = services.AddWorkOrchestrator<string>();

        // Act
        builder.WithHandlerDecorator<string>((sp, handler) => handler);

        // Assert
        await Assert.That(builder.HandlerDecorators).HasCount(1);
    }

    /// <summary>
    /// Verifies that WithHandlerDecorator auto-assigns incrementing order when not specified.
    /// </summary>
    [Test]
    public async Task WithHandlerDecorator_AutoAssignsOrder()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        var builder = services.AddWorkOrchestrator<string>();

        // Act
        builder.WithHandlerDecorator<string>((sp, handler) => handler);
        builder.WithHandlerDecorator<string>((sp, handler) => handler);

        // Assert - first should be 0, second should be 1
        await Assert.That(builder.HandlerDecorators[0].Order).IsEqualTo(0);
        await Assert.That(builder.HandlerDecorators[1].Order).IsEqualTo(1);
    }

    /// <summary>
    /// Verifies that WithHandlerDecorator uses the provided explicit order.
    /// </summary>
    [Test]
    public async Task WithHandlerDecorator_ExplicitOrder_UsesProvidedOrder()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        var builder = services.AddWorkOrchestrator<string>();

        // Act
        builder.WithHandlerDecorator<string>((sp, handler) => handler, order: 42);

        // Assert
        await Assert.That(builder.HandlerDecorators[0].Order).IsEqualTo(42);
    }

    /// <summary>
    /// Verifies that WithHandlerDecorator wraps the handler when the orchestrator is built.
    /// </summary>
    [Test]
    public async Task WithHandlerDecorator_DecoratorWrapsHandler()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var innerHandler = Substitute.For<IWorkHandler<string>>();
        services.AddSingleton(innerHandler);
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        var decoratorApplied = false;
        builder.WithHandlerDecorator<string>((sp, handler) =>
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

        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that WithHandlerDecorator works alongside WithDeadLetterQueue.
    /// </summary>
    [Test]
    public async Task WithHandlerDecorator_ComposesWithBuiltInDecorators()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var innerHandler = Substitute.For<IWorkHandler<string>>();
        services.AddSingleton(innerHandler);
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        var customDecoratorApplied = false;
        builder
            .WithDeadLetterQueue()
            .WithHandlerDecorator<string>((sp, handler) =>
            {
                customDecoratorApplied = true;
                return handler;
            });

        builder.Build();
        var provider = services.BuildServiceProvider();

        // Act
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        // Assert - both DLQ handler decorator and custom decorator should be present
        await Assert.That(customDecoratorApplied).IsTrue();
        // DLQ adds 1 handler decorator, custom adds 1 = at least 2 total
        await Assert.That(builder.HandlerDecorators).HasCount().GreaterThanOrEqualTo(2);

        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that WithHandlerDecorator throws ArgumentNullException when factory is null.
    /// </summary>
    [Test]
    public async Task WithHandlerDecorator_NullFactory_ThrowsArgumentNullException()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = services.AddWorkOrchestrator<string>();

        // Act & Assert
        await Assert.That(() => builder.WithHandlerDecorator<string>(null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that WithHandlerDecorator throws ArgumentNullException when builder is null.
    /// </summary>
    [Test]
    public async Task WithHandlerDecorator_NullBuilder_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.That(() => HandlerDecoratorExtensions.WithHandlerDecorator<string>(
            null!,
            (sp, handler) => handler))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that WithHandlerDecorator returns the builder for method chaining.
    /// </summary>
    [Test]
    public async Task WithHandlerDecorator_ReturnsBuilderForChaining()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = services.AddWorkOrchestrator<string>();

        // Act
        var result = builder.WithHandlerDecorator<string>((sp, handler) => handler);

        // Assert
        await Assert.That(result).IsEqualTo(builder);
    }
}
