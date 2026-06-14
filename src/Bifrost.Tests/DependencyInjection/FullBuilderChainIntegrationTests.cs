// =============================================================================
// <copyright file="FullBuilderChainIntegrationTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;
using System.Reflection;

using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.Core.Events;
using Bifrost.Decorators;
using Bifrost.DependencyInjection;
using Bifrost.Resilience;

using Microsoft.Extensions.DependencyInjection;

using TUnit.Core;

namespace Bifrost.Tests.DependencyInjection;

/// <summary>
/// Full builder chain integration tests verifying the entire API surface
/// works together end-to-end. This is Task 18, the final integration test.
/// </summary>
[Property("Category", "Integration")]
public class FullBuilderChainIntegrationTests
{
    /// <summary>
    /// Verifies that all builder extensions can be chained in forward order and Build() succeeds.
    /// Chain: WithHandler, WithDeadLetterQueue, WithDeadLetterSubscriber, WithEventStream,
    ///     WithHandlerDecorator, WithResilience, WithAutoscaling, Build.
    /// </summary>
    [Test]
    public async Task Builder_AllExtensions_ChainableInAnyOrder()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act - chain all extensions in forward order
        services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1)
            .WithHandler<string, NoOpStringHandler>()
            .WithDeadLetterQueue(opts => opts.MaxRetries = 2)
            .WithDeadLetterSubscriber<string>((item, ct) =>
            {
                return Task.CompletedTask;
            })
            .WithEventStream()
            .WithHandlerDecorator<string>((sp, inner) => new PassthroughHandler(inner))
            .WithResilience()
            .WithAutoscaling()
            .Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        // Assert - orchestrator was created successfully
        await Assert.That(orchestrator).IsNotNull();

        // Verify the decorator chain was applied (outermost should be autoscaling)
        await Assert.That(orchestrator).IsAssignableTo<AutoscalingOrchestrator<string>>();

        // Cleanup
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that all builder extensions can be chained in reverse order and Build() succeeds.
    /// This proves order independence of the builder API.
    /// Chain: WithAutoscaling, WithResilience, WithHandlerDecorator, WithEventStream,
    ///     WithDeadLetterSubscriber, WithDeadLetterQueue, WithHandler, Build.
    /// </summary>
    [Test]
    public async Task Builder_AllExtensions_ReverseOrder_ChainableInAnyOrder()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act - chain all extensions in reverse order
        services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1)
            .WithAutoscaling()
            .WithResilience()
            .WithHandlerDecorator<string>((sp, inner) => new PassthroughHandler(inner))
            .WithEventStream()
            .WithDeadLetterSubscriber<string>((item, ct) =>
            {
                return Task.CompletedTask;
            })
            .WithDeadLetterQueue(opts => opts.MaxRetries = 2)
            .WithHandler<string, NoOpStringHandler>()
            .Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        // Assert - orchestrator was created successfully
        await Assert.That(orchestrator).IsNotNull();

        // Verify autoscaling is still outermost (decorator order is fixed, not call order)
        await Assert.That(orchestrator).IsAssignableTo<AutoscalingOrchestrator<string>>();

        // Cleanup
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies the full chain processes work end-to-end: enqueue work, handler called,
    /// events published through event stream, and DLQ subscriber wired for dead-lettered items.
    /// </summary>
    [Test]
    public async Task Builder_FullChain_ProcessesWorkEndToEnd()
    {
        // Arrange
        var handlerInvocations = new ConcurrentBag<string>();
        var dlSubscriberItems = new ConcurrentBag<DeadLetteredWork<string>>();
        var decoratorInvocations = new ConcurrentBag<string>();

        var services = new ServiceCollection();
        services.AddLogging();

        services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1)
            .WithHandler<string>((work, ct) =>
            {
                handlerInvocations.Add(work);

                // Fail items that start with "fail-" to trigger DLQ
                if (work.StartsWith("fail-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Simulated failure for {work}");
                }

                return ValueTask.CompletedTask;
            })
            .WithDeadLetterQueue(opts => opts.MaxRetries = 0) // Dead-letter on first failure
            .WithDeadLetterSubscriber<string>((item, ct) =>
            {
                dlSubscriberItems.Add(item);
                return Task.CompletedTask;
            })
            .WithEventStream()
            .WithHandlerDecorator<string>((sp, inner) =>
                new TrackingDecoratorHandler(inner, decoratorInvocations))
            .WithResilience()
            .WithAutoscaling()
            .Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        var eventOrchestrator = GetInnerOrchestrator<IEventStreamOrchestrator<string>>(orchestrator);

        await Assert.That(eventOrchestrator).IsNotNull();

        // Subscribe to events before enqueuing. GetEventStreamAsync registers each
        // subscriber channel EAGERLY at call time (not lazily at first enumeration), so
        // creating the streams here — on the test thread, before the enqueue — guarantees
        // no published event can be missed. The bounded subscriber channels buffer events
        // until the collector tasks below begin draining, so the hand-off is race-free and
        // needs no timing delay (the previous Task.Delay was the source of CI flakiness:
        // under load the Task.Run bodies had not yet reached their GetEventStreamAsync call,
        // so they registered after the enqueue and missed the events).
        var enqueuedEvents = new ConcurrentBag<WorkEnqueuedEvent<string>>();
        var completedEvents = new ConcurrentBag<WorkCompletedEvent<string>>();
        var deadLetteredEvents = new ConcurrentBag<WorkDeadLetteredEvent<string>>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var enqueuedStream = eventOrchestrator!.GetEventStreamAsync<WorkEnqueuedEvent<string>>(cancellationToken: cts.Token);
        var completedStream = eventOrchestrator.GetEventStreamAsync<WorkCompletedEvent<string>>(cancellationToken: cts.Token);
        var deadLetteredStream = eventOrchestrator.GetEventStreamAsync<WorkDeadLetteredEvent<string>>(cancellationToken: cts.Token);

        var enqueueTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in enqueuedStream.ConfigureAwait(false))
                {
                    enqueuedEvents.Add(evt);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when CTS fires
            }
        });

        var completedTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in completedStream.ConfigureAwait(false))
                {
                    completedEvents.Add(evt);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when CTS fires
            }
        });

        var dlEventTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in deadLetteredStream.ConfigureAwait(false))
                {
                    deadLetteredEvents.Add(evt);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when CTS fires
            }
        });

        // Act - enqueue a successful item and a failing item. The subscribers are already
        // registered, so these events are captured even if the collector tasks have not yet
        // begun draining their (buffered) channels.
        await orchestrator.EnqueueAsync("success-item").ConfigureAwait(false);
        await orchestrator.EnqueueAsync("fail-doomed").ConfigureAwait(false);

        // Wait for processing
        await Task.Delay(2000).ConfigureAwait(false);

        // Assert - handler was called for both items
        await Assert.That(handlerInvocations).Contains("success-item");
        await Assert.That(handlerInvocations).Contains("fail-doomed");

        // Assert - decorator was invoked (proving WithHandlerDecorator works in the chain)
        await Assert.That(decoratorInvocations.Count).IsGreaterThanOrEqualTo(2);

        // Assert - WorkEnqueuedEvent was published for both items
        await Assert.That(enqueuedEvents.Count).IsGreaterThanOrEqualTo(2);

        // Assert - WorkCompletedEvent was published for the successful item
        await Assert.That(completedEvents.Any(e => e.Work == "success-item" && e.Success)).IsTrue();

        // Assert - WorkDeadLetteredEvent appeared in the event stream
        await Assert.That(deadLetteredEvents.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(deadLetteredEvents.Any(e => e.Work == "fail-doomed")).IsTrue();

        // Assert - DLQ subscriber was called for the failing item
        await Assert.That(dlSubscriberItems.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(dlSubscriberItems.Any(i => Equals(i.Work, "fail-doomed"))).IsTrue();

        // Assert - DLQ has the dead-lettered item
        var dlq = provider.GetRequiredService<IDeadLetterQueue<string>>();
        await Assert.That(dlq.Count).IsGreaterThanOrEqualTo(1);

        // Cleanup
        await cts.CancelAsync().ConfigureAwait(false);
        await orchestrator.DisposeAsync().ConfigureAwait(false);

        // Wait for event subscriber tasks to complete
        try
        {
            await enqueueTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }

        try
        {
            await completedTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }

        try
        {
            await dlEventTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
    }

    /// <summary>
    /// Verifies the old backward-compatible pattern: manually register the handler in DI
    /// and use the builder without calling WithHandler. This is the pre-0.4.0 pattern.
    /// </summary>
    [Test]
    public async Task Builder_WithHandler_BackwardCompatible_ManualRegistration()
    {
        // Arrange - old pattern: manually register handler, no WithHandler call
        var handlerInvocations = new ConcurrentBag<string>();
        var handler = new RecordingStringHandler(handlerInvocations);

        var services = new ServiceCollection();
        services.AddLogging();

        // Manual handler registration (old pattern)
        services.AddSingleton<IWorkHandler<string>>(handler);

        // Build without WithHandler - should fallback to DI-resolved handler
        services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1)
            .WithDeadLetterQueue()
            .WithEventStream()
            .Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        // Act - enqueue work
        await orchestrator.EnqueueAsync("legacy-item").ConfigureAwait(false);
        await Task.Delay(500).ConfigureAwait(false); // Allow processing

        // Assert - handler was called (proving backward compatibility)
        await Assert.That(handlerInvocations).Contains("legacy-item");

        // Assert - event stream is available
        var eventOrchestrator = GetInnerOrchestrator<IEventStreamOrchestrator<string>>(orchestrator);
        await Assert.That(eventOrchestrator).IsNotNull();

        // Cleanup
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    // ---- Helper types ----

    /// <summary>
    /// Uses reflection to walk the decorator chain and find a specific inner orchestrator type.
    /// </summary>
    private static T? GetInnerOrchestrator<T>(IWorkOrchestrator<string> orchestrator)
        where T : class
    {
        var current = orchestrator;
        while (current != null)
        {
            if (current is T target)
            {
                return target;
            }

            var innerField = current.GetType()
                .GetField("_inner", BindingFlags.NonPublic | BindingFlags.Instance);
            current = innerField?.GetValue(current) as IWorkOrchestrator<string>;
        }

        return null;
    }

    /// <summary>
    /// A no-op handler for string work items used in builder chain tests.
    /// </summary>
    private sealed class NoOpStringHandler : IWorkHandler<string>
    {
        public ValueTask HandleAsync(string work, CancellationToken ct)
        {
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A passthrough handler decorator that delegates to the inner handler.
    /// </summary>
    private sealed class PassthroughHandler(IWorkHandler<string> inner) : IWorkHandler<string>
    {
        public ValueTask HandleAsync(string work, CancellationToken ct)
        {
            return inner.HandleAsync(work, ct);
        }
    }

    /// <summary>
    /// A handler decorator that tracks invocations then delegates to the inner handler.
    /// </summary>
    private sealed class TrackingDecoratorHandler(
        IWorkHandler<string> inner,
        ConcurrentBag<string> invocations) : IWorkHandler<string>
    {
        public ValueTask HandleAsync(string work, CancellationToken ct)
        {
            invocations.Add(work);
            return inner.HandleAsync(work, ct);
        }
    }

    /// <summary>
    /// A handler that records invocations for backward compatibility testing.
    /// </summary>
    private sealed class RecordingStringHandler(ConcurrentBag<string> invocations) : IWorkHandler<string>
    {
        public ValueTask HandleAsync(string work, CancellationToken ct)
        {
            invocations.Add(work);
            return ValueTask.CompletedTask;
        }
    }
}
