// =============================================================================
// <copyright file="HandlerExtensionsTests.cs" company="Levelup Software">
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
/// Tests for <see cref="HandlerExtensions"/>.
/// </summary>
[Property("Category", "Unit")]
public class HandlerExtensionsTests
{
    /// <summary>
    /// Verifies that WithHandler type-based overload registers the handler in DI.
    /// </summary>
    [Test]
    public async Task WithHandler_TypeBased_RegistersHandlerInDI()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        // Act
        builder.WithHandler<string, TestHandler>();
        builder.Build();
        var provider = services.BuildServiceProvider();
        var handler = provider.GetService<IWorkHandler<string>>();

        // Assert
        await Assert.That(handler).IsNotNull();
        await Assert.That(handler).IsTypeOf<TestHandler>();

        // Cleanup
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that WithHandler type-based overload registers as Singleton by default.
    /// </summary>
    [Test]
    public async Task WithHandler_TypeBased_DefaultLifetime_IsSingleton()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        // Act
        builder.WithHandler<string, TestHandler>();

        // Assert - check the service descriptor
        var descriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(IWorkHandler<string>));
        await Assert.That(descriptor).IsNotNull();
        await Assert.That(descriptor!.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
    }

    /// <summary>
    /// Verifies that WithHandler type-based overload registers as Scoped when specified.
    /// </summary>
    [Test]
    public async Task WithHandler_TypeBased_ScopedLifetime_RegistersAsScoped()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        // Act
        builder.WithHandler<string, TestHandler>(ServiceLifetime.Scoped);

        // Assert
        var descriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(IWorkHandler<string>));
        await Assert.That(descriptor).IsNotNull();
        await Assert.That(descriptor!.Lifetime).IsEqualTo(ServiceLifetime.Scoped);
    }

    /// <summary>
    /// Verifies that WithHandler type-based overload registers as Transient when specified.
    /// </summary>
    [Test]
    public async Task WithHandler_TypeBased_TransientLifetime_RegistersAsTransient()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        // Act
        builder.WithHandler<string, TestHandler>(ServiceLifetime.Transient);

        // Assert
        var descriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(IWorkHandler<string>));
        await Assert.That(descriptor).IsNotNull();
        await Assert.That(descriptor!.Lifetime).IsEqualTo(ServiceLifetime.Transient);
    }

    /// <summary>
    /// Verifies that WithHandler type-based overload sets the HandlerRegistered flag.
    /// </summary>
    [Test]
    public async Task WithHandler_TypeBased_SetsHandlerRegisteredFlag()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>();

        // Act
        builder.WithHandler<string, TestHandler>();

        // Assert
        await Assert.That(builder.HandlerRegistered).IsTrue();
    }

    /// <summary>
    /// Verifies that calling WithHandler twice throws InvalidOperationException.
    /// </summary>
    [Test]
    public async Task WithHandler_CalledTwice_ThrowsInvalidOperationException()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>();
        builder.WithHandler<string, TestHandler>();

        // Act & Assert
        await Assert.That(() => builder.WithHandler<string, TestHandler>())
            .Throws<InvalidOperationException>();
    }

    /// <summary>
    /// Verifies that WithHandler type-based overload throws when builder is null.
    /// </summary>
    [Test]
    public async Task WithHandler_TypeBased_NullBuilder_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.That(() =>
                HandlerExtensions.WithHandler<string, TestHandler>(null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that WithHandler type-based overload returns the builder for chaining.
    /// </summary>
    [Test]
    public async Task WithHandler_TypeBased_ChainsWithOtherExtensions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        // Act
        var result = builder.WithHandler<string, TestHandler>();

        // Assert
        await Assert.That(result).IsEqualTo(builder);
    }

    // =========================================================================
    // Factory overload tests (Task 6)
    // =========================================================================

    /// <summary>
    /// Verifies that WithHandler factory overload registers a handler created by the factory.
    /// </summary>
    [Test]
    public async Task WithHandler_Factory_RegistersFromFactory()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);
        var factoryHandler = new TestHandler();

        // Act
        builder.WithHandler<string>(sp => factoryHandler);
        builder.Build();
        var provider = services.BuildServiceProvider();
        var resolved = provider.GetService<IWorkHandler<string>>();

        // Assert
        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved).IsSameReferenceAs(factoryHandler);

        // Cleanup
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that WithHandler factory overload respects the specified lifetime.
    /// </summary>
    [Test]
    public async Task WithHandler_Factory_RespectsLifetime()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        // Act
        builder.WithHandler<string>(sp => new TestHandler(), ServiceLifetime.Transient);

        // Assert
        var descriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(IWorkHandler<string>));
        await Assert.That(descriptor).IsNotNull();
        await Assert.That(descriptor!.Lifetime).IsEqualTo(ServiceLifetime.Transient);
    }

    /// <summary>
    /// Verifies that WithHandler factory overload throws when factory is null.
    /// </summary>
    [Test]
    public async Task WithHandler_Factory_NullFactory_ThrowsArgumentNullException()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = services.AddWorkOrchestrator<string>();

        // Act & Assert
        await Assert.That(() =>
                builder.WithHandler<string>(
                    (Func<IServiceProvider, IWorkHandler<string>>)null!))
            .Throws<ArgumentNullException>();
    }

    // =========================================================================
    // Delegate overload tests (Task 6)
    // =========================================================================

    /// <summary>
    /// Verifies that WithHandler delegate overload registers an InlineDelegateWorkHandler.
    /// </summary>
    [Test]
    public async Task WithHandler_Delegate_RegistersInlineDelegateHandler()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        // Act
        builder.WithHandler<string>((work, ct) => ValueTask.CompletedTask);
        builder.Build();
        var provider = services.BuildServiceProvider();
        var handler = provider.GetService<IWorkHandler<string>>();

        // Assert
        await Assert.That(handler).IsNotNull();
        await Assert.That(handler!.GetType().Name).Contains("InlineDelegateWorkHandler");

        // Cleanup
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that WithHandler delegate overload actually invokes the delegate on processing.
    /// </summary>
    [Test]
    public async Task WithHandler_Delegate_HandlerInvokesDelegateOnProcess()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        var invoked = false;
        builder.WithHandler<string>((work, ct) =>
        {
            invoked = true;
            return ValueTask.CompletedTask;
        });
        builder.Build();
        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        // Act
        await orchestrator.EnqueueAsync("test-work").ConfigureAwait(false);
        await Task.Delay(200).ConfigureAwait(false); // Allow processing

        // Assert
        await Assert.That(invoked).IsTrue();

        // Cleanup
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that WithHandler delegate overload throws when delegate is null.
    /// </summary>
    [Test]
    public async Task WithHandler_Delegate_NullDelegate_ThrowsArgumentNullException()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = services.AddWorkOrchestrator<string>();

        // Act & Assert
        await Assert.That(() =>
                builder.WithHandler<string>(
                    (Func<string, CancellationToken, ValueTask>)null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that WithHandler delegate overload registers as Singleton by default.
    /// </summary>
    [Test]
    public async Task WithHandler_Delegate_RegistersAsSingleton()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        // Act
        builder.WithHandler<string>((work, ct) => ValueTask.CompletedTask);

        // Assert
        var descriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(IWorkHandler<string>));
        await Assert.That(descriptor).IsNotNull();
        await Assert.That(descriptor!.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
    }

    /// <summary>
    /// A simple test handler for string work items.
    /// </summary>
    private class TestHandler : IWorkHandler<string>
    {
        public ValueTask HandleAsync(string work, CancellationToken ct)
        {
            return ValueTask.CompletedTask;
        }
    }
}
