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
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        var builder = services.AddWorkOrchestrator<string>();

        builder.WithEventStream();
        builder.Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetService<IWorkOrchestrator<string>>();

        await Assert.That(orchestrator).IsNotNull();
        await Assert.That(orchestrator).IsAssignableTo<IEventStreamOrchestrator<string>>();

        if (orchestrator is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies that WithEventStream returns the builder for chaining.
    /// </summary>
    [Test]
    public async Task WithEventStream_ReturnsBuilder()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());
        var builder = services.AddWorkOrchestrator<string>();

        var result = builder.WithEventStream();

        await Assert.That(result).IsSameReferenceAs(builder);
    }

    /// <summary>
    /// Verifies that the decorated orchestrator can stream events.
    /// </summary>
    /// <remarks>
    /// The event stream is a broadcast, so a subscriber gets only the events published after it starts.
    /// The test starts the subscriber first, then waits 100 ms so that it can start before the enqueue.
    /// </remarks>
    [Test]
    public async Task WithEventStream_OrchestratorCanStreamEvents()
    {
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

        var subscriberTask = Task.Run(async () =>
        {
            await foreach (var evt in eventStreamOrchestrator.GetEventStreamAsync<WorkEnqueuedEvent<string>>(cancellationToken: cts.Token))
            {
                receivedEvent.TrySetResult(evt);
                break;
            }
        });

        await Task.Delay(100);

        await orchestrator.EnqueueAsync("test-work");

        var evt = await receivedEvent.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Assert.That(evt.Work).IsEqualTo("test-work");

        await orchestrator.DisposeAsync();
    }

    /// <summary>
    /// Verifies that WithEventStream can be chained with Build.
    /// </summary>
    [Test]
    public async Task WithEventStream_CanChainWithBuild()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());

        services.AddWorkOrchestrator<string>()
            .WithEventStream()
            .Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetService<IWorkOrchestrator<string>>();

        await Assert.That(orchestrator).IsNotNull();
        await Assert.That(orchestrator).IsAssignableTo<IEventStreamOrchestrator<string>>();

        if (orchestrator is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
    }
}