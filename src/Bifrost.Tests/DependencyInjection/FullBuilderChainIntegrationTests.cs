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
        var services = new ServiceCollection();
        services.AddLogging();

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

        await Assert.That(orchestrator).IsNotNull();

        await Assert.That(orchestrator).IsAssignableTo<AutoscalingOrchestrator<string>>();

        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that all builder extensions can be chained in reverse order and Build() succeeds.
    /// This proves order independence of the builder API.
    /// Chain: WithAutoscaling, WithResilience, WithHandlerDecorator, WithEventStream,
    ///     WithDeadLetterSubscriber, WithDeadLetterQueue, WithHandler, Build.
    /// </summary>
    /// <remarks>
    /// Autoscaling stays the outermost decorator, because the decorator order is fixed and does not
    /// follow the call order.
    /// </remarks>
    [Test]
    public async Task Builder_AllExtensions_ReverseOrder_ChainableInAnyOrder()
    {
        var services = new ServiceCollection();
        services.AddLogging();

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

        await Assert.That(orchestrator).IsNotNull();

        await Assert.That(orchestrator).IsAssignableTo<AutoscalingOrchestrator<string>>();

        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies the full chain processes work end-to-end: enqueue work, handler called,
    /// events published through event stream, and DLQ subscriber wired for dead-lettered items.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The handler fails each item that starts with <c>fail-</c>. With <c>MaxRetries = 0</c>, the DLQ
    /// dead-letters that item on its first failure.
    /// </para>
    /// <para>
    /// The test subscribes to events before the enqueue. <c>GetEventStreamAsync</c> registers each
    /// subscriber channel eagerly at call time, not lazily at the first enumeration. The test creates
    /// the streams on the test thread before the enqueue, so no published event can be missed. The
    /// bounded subscriber channels buffer events until the collector tasks start to drain them. Thus
    /// the hand-off has no race and needs no timing delay.
    /// </para>
    /// <para>
    /// An earlier <c>Task.Delay</c> caused CI flakiness. Under load, the <c>Task.Run</c> bodies had not
    /// yet reached their <c>GetEventStreamAsync</c> call. They registered after the enqueue and missed
    /// the events.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Builder_FullChain_ProcessesWorkEndToEnd()
    {
        var handlerInvocations = new ConcurrentBag<string>();
        var dlSubscriberItems = new ConcurrentBag<DeadLetteredWork<string>>();
        var decoratorInvocations = new ConcurrentBag<string>();

        var services = new ServiceCollection();
        services.AddLogging();

        services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1)
            .WithHandler<string>((work, ct) =>
            {
                handlerInvocations.Add(work);

                if (work.StartsWith("fail-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Simulated failure for {work}");
                }

                return ValueTask.CompletedTask;
            })
            .WithDeadLetterQueue(opts => opts.MaxRetries = 0)
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
            }
        });

        await orchestrator.EnqueueAsync("success-item").ConfigureAwait(false);
        await orchestrator.EnqueueAsync("fail-doomed").ConfigureAwait(false);

        await Task.Delay(2000).ConfigureAwait(false);

        await Assert.That(handlerInvocations).Contains("success-item");
        await Assert.That(handlerInvocations).Contains("fail-doomed");

        await Assert.That(decoratorInvocations.Count).IsGreaterThanOrEqualTo(2);

        await Assert.That(enqueuedEvents.Count).IsGreaterThanOrEqualTo(2);

        await Assert.That(completedEvents.Any(e => e.Work == "success-item" && e.Success)).IsTrue();

        await Assert.That(deadLetteredEvents.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(deadLetteredEvents.Any(e => e.Work == "fail-doomed")).IsTrue();

        await Assert.That(dlSubscriberItems.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(dlSubscriberItems.Any(i => Equals(i.Work, "fail-doomed"))).IsTrue();

        var dlq = provider.GetRequiredService<IDeadLetterQueue<string>>();
        await Assert.That(dlq.Count).IsGreaterThanOrEqualTo(1);

        await cts.CancelAsync().ConfigureAwait(false);
        await orchestrator.DisposeAsync().ConfigureAwait(false);

        try
        {
            await enqueueTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await completedTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await dlEventTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Verifies the old backward-compatible pattern: manually register the handler in DI
    /// and use the builder without calling WithHandler. This is the pre-0.4.0 pattern.
    /// </summary>
    /// <remarks>
    /// Without <c>WithHandler</c>, the builder falls back to the handler that is registered in DI.
    /// </remarks>
    [Test]
    public async Task Builder_WithHandler_BackwardCompatible_ManualRegistration()
    {
        var handlerInvocations = new ConcurrentBag<string>();
        var handler = new RecordingStringHandler(handlerInvocations);

        var services = new ServiceCollection();
        services.AddLogging();

        services.AddSingleton<IWorkHandler<string>>(handler);

        services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1)
            .WithDeadLetterQueue()
            .WithEventStream()
            .Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        await orchestrator.EnqueueAsync("legacy-item").ConfigureAwait(false);
        await Task.Delay(500).ConfigureAwait(false);

        await Assert.That(handlerInvocations).Contains("legacy-item");

        var eventOrchestrator = GetInnerOrchestrator<IEventStreamOrchestrator<string>>(orchestrator);
        await Assert.That(eventOrchestrator).IsNotNull();

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
