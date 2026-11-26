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
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

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
    /// The test calls <c>GetEventStreamAsync</c> before the enqueue. The call registers the subscriber, so
    /// the event is buffered even if the reader task starts late.
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
        using var cts = new CancellationTokenSource(WaitTimeout);

        var stream = eventStreamOrchestrator.GetEventStreamAsync<WorkEnqueuedEvent<string>>(cancellationToken: cts.Token);
        var subscriberTask = Task.Run(async () =>
        {
            await foreach (var evt in stream)
            {
                receivedEvent.TrySetResult(evt);
                break;
            }
        });

        await orchestrator.EnqueueAsync("test-work");

        var evt = await receivedEvent.Task.WaitAsync(WaitTimeout);
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