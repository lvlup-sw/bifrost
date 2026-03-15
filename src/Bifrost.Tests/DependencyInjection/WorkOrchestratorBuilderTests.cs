// =============================================================================
// <copyright file="WorkOrchestratorBuilderTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.DependencyInjection;
using Bifrost.Handlers;

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

    /// <summary>
    /// Verifies that Build falls back to resolving IWorkHandler from the service provider
    /// when WithHandler was not called (backward compatibility).
    /// </summary>
    [Test]
    public async Task Build_WithoutHandlerRegistered_FallsBackToServiceProvider()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var handler = Substitute.For<IWorkHandler<string>>();
        services.AddSingleton(handler);
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        // Act
        builder.Build();
        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        // Assert - orchestrator resolves successfully, handler flag is not set
        await Assert.That(orchestrator).IsNotNull();
        await Assert.That(builder.HandlerRegistered).IsFalse();

        // Cleanup
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that Build uses ScopedHandlerProxy when HandlerLifetime is Scoped.
    /// </summary>
    [Test]
    public async Task Build_WithScopedHandlerLifetime_UsesScopedHandlerProxy()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Register the actual handler as scoped (simulating what WithHandler would do)
        services.AddScoped<IWorkHandler<string>, TestStringHandler>();

        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);
        builder.HandlerLifetime = ServiceLifetime.Scoped;
        builder.HandlerRegistered = true;

        // Act
        builder.Build();
        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        // Enqueue and process a work item to verify the proxy resolves handlers from scope
        await orchestrator.EnqueueAsync("test-item").ConfigureAwait(false);
        await Task.Delay(200).ConfigureAwait(false); // Allow processing

        // Assert - If we got here without exception, the scoped handler proxy resolved correctly
        await Assert.That(orchestrator).IsNotNull();
        await Assert.That(TestStringHandler.HandleCallCount).IsGreaterThan(0);

        // Cleanup
        TestStringHandler.Reset();
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that PostBuildActions are executed during Build.
    /// </summary>
    [Test]
    public async Task PostBuildActions_ExecutedDuringBuild()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        var actionExecuted = false;
        builder.PostBuildActions.Add(sp =>
        {
            actionExecuted = true;
        });

        // Act
        builder.Build();
        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        // Assert
        await Assert.That(actionExecuted).IsTrue();

        // Cleanup
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Test handler that tracks invocations for verifying scoped resolution.
    /// </summary>
    private class TestStringHandler : IWorkHandler<string>
    {
        private static int _handleCallCount;

        public static int HandleCallCount => _handleCallCount;

        public static void Reset() => _handleCallCount = 0;

        public ValueTask HandleAsync(string work, CancellationToken ct)
        {
            Interlocked.Increment(ref _handleCallCount);
            return ValueTask.CompletedTask;
        }
    }
}