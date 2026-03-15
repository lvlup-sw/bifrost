// =============================================================================
// <copyright file="ScopedHandlerIntegrationTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;

using Bifrost.Core;
using Bifrost.Core.Events;
using Bifrost.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;

using TUnit.Core;

namespace Bifrost.Tests.DependencyInjection;

/// <summary>
/// Integration tests verifying scoped handler resolution per work item.
/// </summary>
[Property("Category", "Integration")]
public class ScopedHandlerIntegrationTests
{
    /// <summary>
    /// Verifies that a scoped handler resolves a new instance from a fresh scope per work item.
    /// </summary>
    [Test]
    public async Task WithHandler_Scoped_ResolvesFromScope_PerWorkItem()
    {
        // Arrange
        var tracker = new InstanceTracker();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(tracker);
        services.AddScoped<ScopedTrackingHandler>();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        // Register via factory that resolves from scope to get the ScopedTrackingHandler
        builder.WithHandler<string>(sp => sp.GetRequiredService<ScopedTrackingHandler>(),
            ServiceLifetime.Scoped);
        builder.Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        // Act - enqueue 3 items
        await orchestrator.EnqueueAsync("item-1").ConfigureAwait(false);
        await orchestrator.EnqueueAsync("item-2").ConfigureAwait(false);
        await orchestrator.EnqueueAsync("item-3").ConfigureAwait(false);
        await Task.Delay(500).ConfigureAwait(false); // Allow processing

        // Assert - should have 3 distinct handler instance IDs (one per scope)
        await Assert.That(tracker.InstanceIds.Count).IsGreaterThanOrEqualTo(3);
        var distinctInstances = tracker.InstanceIds.Distinct().Count();
        await Assert.That(distinctInstances).IsGreaterThanOrEqualTo(3);

        // Cleanup
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that a scoped handler works with the dead letter queue.
    /// Each retry should create a fresh scope.
    /// </summary>
    [Test]
    public async Task WithHandler_Scoped_WorksWithDeadLetterQueue()
    {
        // Arrange
        var tracker = new InstanceTracker();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(tracker);
        services.AddScoped<FailingScopedTrackingHandler>();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        builder.WithHandler<string>(sp => sp.GetRequiredService<FailingScopedTrackingHandler>(),
            ServiceLifetime.Scoped);
        builder.WithDeadLetterQueue(opts => opts.MaxRetries = 2);
        builder.Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        // Act - enqueue 1 item that will fail (1 initial + 2 retries = 3 scope creations)
        await orchestrator.EnqueueAsync("failing-item").ConfigureAwait(false);
        await Task.Delay(500).ConfigureAwait(false); // Allow processing + retries

        // Assert - should have at least 3 handler instances (initial + 2 retries)
        var distinctInstances = tracker.InstanceIds.Distinct().Count();
        await Assert.That(distinctInstances).IsGreaterThanOrEqualTo(3);

        // Cleanup
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that a scoped handler works alongside the event stream.
    /// </summary>
    [Test]
    public async Task WithHandler_Scoped_WorksWithEventStream()
    {
        // Arrange
        var tracker = new InstanceTracker();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(tracker);
        services.AddScoped<ScopedTrackingHandler>();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        builder.WithHandler<string>(sp => sp.GetRequiredService<ScopedTrackingHandler>(),
            ServiceLifetime.Scoped);
        builder.WithEventStream();
        builder.Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        var eventOrchestrator = orchestrator as IEventStreamOrchestrator<string>;

        // Collect events
        var events = new ConcurrentBag<IOrchestratorEvent>();
        var eventTask = Task.Run(async () =>
        {
            if (eventOrchestrator == null)
            {
                return;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await foreach (var evt in eventOrchestrator
                    .GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token)
                    .ConfigureAwait(false))
                {
                    events.Add(evt);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        });

        // Act
        await orchestrator.EnqueueAsync("test-item").ConfigureAwait(false);
        await Task.Delay(500).ConfigureAwait(false); // Allow processing

        // Assert - events should have been published
        await Assert.That(events.Count).IsGreaterThan(0);

        // Cleanup
        await orchestrator.DisposeAsync().ConfigureAwait(false);
        await eventTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that a singleton handler resolves once (control test).
    /// </summary>
    [Test]
    public async Task WithHandler_Singleton_ResolvesOnce()
    {
        // Arrange
        var tracker = new InstanceTracker();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(tracker);
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        // Use factory overload for singleton -- the factory is called once, creating one instance
        builder.WithHandler<string>(sp =>
            new SingletonTrackingHandler(sp.GetRequiredService<InstanceTracker>()));
        builder.Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        // Act - enqueue 3 items
        await orchestrator.EnqueueAsync("item-1").ConfigureAwait(false);
        await orchestrator.EnqueueAsync("item-2").ConfigureAwait(false);
        await orchestrator.EnqueueAsync("item-3").ConfigureAwait(false);
        await Task.Delay(500).ConfigureAwait(false); // Allow processing

        // Assert - should have 3 invocations but exactly 1 distinct handler instance (singleton)
        await Assert.That(tracker.InstanceIds.Count).IsGreaterThanOrEqualTo(3);
        var distinctInstances = tracker.InstanceIds.Distinct().Count();
        await Assert.That(distinctInstances).IsEqualTo(1);

        // Cleanup
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Per-test tracker for handler instance IDs. Not static -- avoids test interference.
    /// </summary>
    private sealed class InstanceTracker
    {
        public ConcurrentBag<Guid> InstanceIds { get; } = [];
    }

    /// <summary>
    /// A scoped handler that records its instance ID on each invocation.
    /// </summary>
    private sealed class ScopedTrackingHandler : IWorkHandler<string>
    {
        private readonly InstanceTracker _tracker;
        private readonly Guid _id = Guid.NewGuid();

        public ScopedTrackingHandler(InstanceTracker tracker)
        {
            _tracker = tracker;
        }

        public ValueTask HandleAsync(string work, CancellationToken ct)
        {
            _tracker.InstanceIds.Add(_id);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A singleton handler that records its instance ID on each invocation.
    /// </summary>
    private sealed class SingletonTrackingHandler : IWorkHandler<string>
    {
        private readonly InstanceTracker _tracker;
        private readonly Guid _id = Guid.NewGuid();

        public SingletonTrackingHandler(InstanceTracker tracker)
        {
            _tracker = tracker;
        }

        public ValueTask HandleAsync(string work, CancellationToken ct)
        {
            _tracker.InstanceIds.Add(_id);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A scoped handler that always fails and tracks instances, for DLQ retry testing.
    /// </summary>
    private sealed class FailingScopedTrackingHandler : IWorkHandler<string>
    {
        private readonly InstanceTracker _tracker;
        private readonly Guid _id = Guid.NewGuid();

        public FailingScopedTrackingHandler(InstanceTracker tracker)
        {
            _tracker = tracker;
        }

        public ValueTask HandleAsync(string work, CancellationToken ct)
        {
            _tracker.InstanceIds.Add(_id);
            throw new InvalidOperationException("Simulated failure");
        }
    }
}
