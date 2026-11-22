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
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        builder.WithHandler<string, TestHandler>();
        builder.Build();
        var provider = services.BuildServiceProvider();
        var handler = provider.GetService<IWorkHandler<string>>();

        await Assert.That(handler).IsNotNull();
        await Assert.That(handler).IsTypeOf<TestHandler>();

        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that WithHandler type-based overload registers as Singleton by default.
    /// </summary>
    [Test]
    public async Task WithHandler_TypeBased_DefaultLifetime_IsSingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

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
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        builder.WithHandler<string, TestHandler>(ServiceLifetime.Scoped);

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
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        builder.WithHandler<string, TestHandler>(ServiceLifetime.Transient);

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
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>();

        builder.WithHandler<string, TestHandler>();

        await Assert.That(builder.HandlerRegistered).IsTrue();
    }

    /// <summary>
    /// Verifies that calling WithHandler twice throws InvalidOperationException.
    /// </summary>
    [Test]
    public async Task WithHandler_CalledTwice_ThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>();
        builder.WithHandler<string, TestHandler>();

        await Assert.That(() => builder.WithHandler<string, TestHandler>())
            .Throws<InvalidOperationException>();
    }

    /// <summary>
    /// Verifies that WithHandler type-based overload throws when builder is null.
    /// </summary>
    [Test]
    public async Task WithHandler_TypeBased_NullBuilder_ThrowsArgumentNullException()
    {
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
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        var result = builder.WithHandler<string, TestHandler>();

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
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);
        var factoryHandler = new TestHandler();

        builder.WithHandler<string>(sp => factoryHandler);
        builder.Build();
        var provider = services.BuildServiceProvider();
        var resolved = provider.GetService<IWorkHandler<string>>();

        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved).IsSameReferenceAs(factoryHandler);

        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that WithHandler factory overload respects the specified lifetime.
    /// </summary>
    [Test]
    public async Task WithHandler_Factory_RespectsLifetime()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        builder.WithHandler<string>(sp => new TestHandler(), ServiceLifetime.Transient);

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
        var services = new ServiceCollection();
        var builder = services.AddWorkOrchestrator<string>();

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
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        builder.WithHandler<string>((work, ct) => ValueTask.CompletedTask);
        builder.Build();
        var provider = services.BuildServiceProvider();
        var handler = provider.GetService<IWorkHandler<string>>();

        await Assert.That(handler).IsNotNull();
        await Assert.That(handler!.GetType().Name).Contains("InlineDelegateWorkHandler");

        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that WithHandler delegate overload actually invokes the delegate on processing.
    /// </summary>
    [Test]
    public async Task WithHandler_Delegate_HandlerInvokesDelegateOnProcess()
    {
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

        await orchestrator.EnqueueAsync("test-work").ConfigureAwait(false);
        await Task.Delay(200).ConfigureAwait(false); // Allow processing

        await Assert.That(invoked).IsTrue();

        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that WithHandler delegate overload throws when delegate is null.
    /// </summary>
    [Test]
    public async Task WithHandler_Delegate_NullDelegate_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection();
        var builder = services.AddWorkOrchestrator<string>();

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
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        builder.WithHandler<string>((work, ct) => ValueTask.CompletedTask);

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
