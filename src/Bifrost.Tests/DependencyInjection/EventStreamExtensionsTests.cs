// =============================================================================
// <copyright file="EventStreamExtensionsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Core.Events;
using Bifrost.Decorators;
using Bifrost.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Bifrost.Tests.DependencyInjection;

/// <summary>
/// Tests for <see cref="EventStreamExtensions"/>.
/// </summary>
public class EventStreamExtensionsTests
{
    /// <summary>
    /// Verifies that WithEventStream adds the decorator to the builder.
    /// </summary>
    [Test]
    public async Task WithEventStream_AddsDecorator()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        var builder = services.AddWorkOrchestrator<string>();

        // Act
        builder.WithEventStream();
        builder.Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetService<IWorkOrchestrator<string>>();

        // Assert
        await Assert.That(orchestrator).IsNotNull();
        await Assert.That(orchestrator).IsAssignableTo<IEventStreamOrchestrator<string>>();

        // Cleanup
        if (orchestrator is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies that WithEventStream returns the builder for chaining.
    /// </summary>
    [Test]
    public async Task WithEventStream_ReturnsBuilder()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        var builder = services.AddWorkOrchestrator<string>();

        // Act
        var result = builder.WithEventStream();

        // Assert
        await Assert.That(result).IsSameReferenceAs(builder);
    }

    /// <summary>
    /// Verifies that the decorated orchestrator can stream events.
    /// </summary>
    [Test]
    public async Task WithEventStream_OrchestratorCanStreamEvents()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        var builder = services.AddWorkOrchestrator<string>();
        builder.WithEventStream();
        builder.Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        var eventStreamOrchestrator = (IEventStreamOrchestrator<string>)orchestrator;

        var receivedEvent = new TaskCompletionSource<WorkEnqueuedEvent<string>>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Start subscriber BEFORE enqueuing (broadcast pattern)
        var subscriberTask = Task.Run(async () =>
        {
            await foreach (var evt in eventStreamOrchestrator.GetEventStreamAsync<WorkEnqueuedEvent<string>>(cancellationToken: cts.Token).ConfigureAwait(false))
            {
                receivedEvent.TrySetResult(evt);
                break;
            }
        });

        // Give subscriber time to start
        await Task.Delay(100).ConfigureAwait(false);

        // Act - enqueue work
        await orchestrator.EnqueueAsync("test-work").ConfigureAwait(false);

        // Assert - verify event can be consumed
        var evt = await receivedEvent.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        await Assert.That(evt.Work).IsEqualTo("test-work");

        // Cleanup
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that WithEventStream can be chained with Build.
    /// </summary>
    [Test]
    public async Task WithEventStream_CanChainWithBuild()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());

        // Act - chain WithEventStream with Build
        services.AddWorkOrchestrator<string>()
            .WithEventStream()
            .Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetService<IWorkOrchestrator<string>>();

        // Assert
        await Assert.That(orchestrator).IsNotNull();
        await Assert.That(orchestrator).IsAssignableTo<IEventStreamOrchestrator<string>>();

        // Cleanup
        if (orchestrator is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }
}
