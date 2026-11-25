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
    /// <remarks>
    /// Each scope creates one handler instance, so three work items give three distinct instance IDs.
    /// </remarks>
    [Test]
    public async Task WithHandler_Scoped_ResolvesFromScope_PerWorkItem()
    {
        var tracker = new InstanceTracker();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(tracker);
        services.AddScoped<ScopedTrackingHandler>();
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        builder.WithHandler<string>(sp => sp.GetRequiredService<ScopedTrackingHandler>(),
            ServiceLifetime.Scoped);
        builder.Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        await orchestrator.EnqueueAsync("item-1");
        await orchestrator.EnqueueAsync("item-2");
        await orchestrator.EnqueueAsync("item-3");
        await Task.Delay(500);

        await Assert.That(tracker.InstanceIds.Count).IsGreaterThanOrEqualTo(3);
        var distinctInstances = tracker.InstanceIds.Distinct().Count();
        await Assert.That(distinctInstances).IsGreaterThanOrEqualTo(3);

        await orchestrator.DisposeAsync();
    }

    /// <summary>
    /// Verifies that a scoped handler works with the dead letter queue.
    /// Each retry should create a fresh scope.
    /// </summary>
    /// <remarks>
    /// The item always fails. One initial attempt plus two retries gives three scope creations, so the
    /// test expects at least three handler instances.
    /// </remarks>
    [Test]
    public async Task WithHandler_Scoped_WorksWithDeadLetterQueue()
    {
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

        await orchestrator.EnqueueAsync("failing-item");
        await Task.Delay(500);

        var distinctInstances = tracker.InstanceIds.Distinct().Count();
        await Assert.That(distinctInstances).IsGreaterThanOrEqualTo(3);

        await orchestrator.DisposeAsync();
    }

    /// <summary>
    /// Verifies that a scoped handler works alongside the event stream.
    /// </summary>
    /// <remarks>
    /// The consumer starts before the enqueue, and the test waits until the subscriber channel is
    /// registered. After <c>DrainAsync</c>, a 100 ms delay lets event propagation finish, because the
    /// subscriber notification is fire-and-forget.
    /// </remarks>
    [Test]
    public async Task WithHandler_Scoped_WorksWithEventStream()
    {
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

        var events = new ConcurrentBag<IOrchestratorEvent>();
        using var eventCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var subscriberReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventTask = Task.Run(async () =>
        {
            if (eventOrchestrator == null)
            {
                subscriberReady.TrySetResult();
                return;
            }

            var enumerable = eventOrchestrator
                .GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: eventCts.Token);
            var enumerator = enumerable.GetAsyncEnumerator(eventCts.Token);

            subscriberReady.TrySetResult();

            try
            {
                while (await enumerator.MoveNextAsync())
                {
                    events.Add(enumerator.Current);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                await enumerator.DisposeAsync();
            }
        });

        await subscriberReady.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await orchestrator.EnqueueAsync("test-item");

        await orchestrator.DrainAsync();

        await Task.Delay(100);
        await eventCts.CancelAsync();
        await eventTask;

        await Assert.That(events.Count).IsGreaterThan(0);

        await orchestrator.DisposeAsync();
    }

    /// <summary>
    /// Verifies that a singleton handler resolves once (control test).
    /// </summary>
    /// <remarks>
    /// For a singleton, the factory runs once and creates one instance. Three work items give three
    /// invocations but exactly one distinct handler instance.
    /// </remarks>
    [Test]
    public async Task WithHandler_Singleton_ResolvesOnce()
    {
        var tracker = new InstanceTracker();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(tracker);
        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);

        builder.WithHandler<string>(sp =>
            new SingletonTrackingHandler(sp.GetRequiredService<InstanceTracker>()));
        builder.Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        await orchestrator.EnqueueAsync("item-1");
        await orchestrator.EnqueueAsync("item-2");
        await orchestrator.EnqueueAsync("item-3");
        await Task.Delay(500);

        await Assert.That(tracker.InstanceIds.Count).IsGreaterThanOrEqualTo(3);
        var distinctInstances = tracker.InstanceIds.Distinct().Count();
        await Assert.That(distinctInstances).IsEqualTo(1);

        await orchestrator.DisposeAsync();
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
