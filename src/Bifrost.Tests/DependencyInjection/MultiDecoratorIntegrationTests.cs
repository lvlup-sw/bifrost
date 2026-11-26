// =============================================================================
// <copyright file="MultiDecoratorIntegrationTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Reflection;

using Bifrost.Autoscaling;
using Bifrost.Core;
using Bifrost.Core.Events;
using Bifrost.Decorators;
using Bifrost.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;

using TUnit.Core;

namespace Bifrost.Tests.DependencyInjection;

/// <summary>
/// Integration tests verifying the full decorator chain works end-to-end
/// without mocking the inner orchestrator (L10).
/// </summary>
[Property("Category", "Integration")]
public class MultiDecoratorIntegrationTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies the full decorator chain processes work end-to-end:
    /// handler called, events published, metrics tracked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The decorator chain is <c>AutoscalingOrchestrator</c> (outer) over <c>EventStreamOrchestrator</c>
    /// (inner). The test reaches the inner decorator through reflection.
    /// </para>
    /// <para>
    /// The test calls <c>GetEventStreamAsync</c> for both subscribers before the enqueue. The call registers
    /// each subscriber, so the events are buffered even if a reader task or the worker starts late.
    /// </para>
    /// </remarks>
    [Test]
    public async Task FullDecoratorChain_ProcessesWorkEndToEnd()
    {
        var handlerInvocations = new List<string>();
        var handler = new RecordingHandler(handlerInvocations);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkHandler<string>>(handler);

        services.AddWorkOrchestrator<string>()
            .WithEventStream()
            .WithAutoscaling()
            .Build();

        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        await Assert.That(orchestrator).IsAssignableTo<AutoscalingOrchestrator<string>>();

        var eventStream = GetInnerOrchestrator<IEventStreamOrchestrator<string>>(orchestrator);
        await Assert.That(eventStream).IsNotNull();

        var enqueuedEvents = new List<WorkEnqueuedEvent<string>>();
        var completedEvents = new List<WorkCompletedEvent<string>>();
        using var cts = new CancellationTokenSource(WaitTimeout);

        var enqueuedStream = eventStream!.GetEventStreamAsync<WorkEnqueuedEvent<string>>(cancellationToken: cts.Token);
        var enqueueSubscriber = Task.Run(async () =>
        {
            await foreach (var evt in enqueuedStream)
            {
                enqueuedEvents.Add(evt);
                if (enqueuedEvents.Count >= 1) break;
            }
        });

        var completedStream = eventStream.GetEventStreamAsync<WorkCompletedEvent<string>>(cancellationToken: cts.Token);
        var completedSubscriber = Task.Run(async () =>
        {
            await foreach (var evt in completedStream)
            {
                completedEvents.Add(evt);
                if (completedEvents.Count >= 1) break;
            }
        });

        var workerFunc = orchestrator.CreateWorkerFunction();
        var workerTask = Task.Run(() => workerFunc("integration-worker", cts.Token));

        await orchestrator.EnqueueAsync("integration-test-item");

        await Task.WhenAll(enqueueSubscriber, completedSubscriber)
            .WaitAsync(WaitTimeout);

        await Assert.That(handlerInvocations).Contains("integration-test-item");

        await Assert.That(enqueuedEvents.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(enqueuedEvents[0].Work).IsEqualTo("integration-test-item");

        await Assert.That(completedEvents.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(completedEvents[0].Work).IsEqualTo("integration-test-item");
        await Assert.That(completedEvents[0].Success).IsTrue();

        var metrics = provider.GetRequiredKeyedService<IWorkerMetrics>(typeof(string));
        await Assert.That(metrics).IsNotNull();

        await cts.CancelAsync();
        try
        {
            await workerTask.WaitAsync(WaitTimeout);
        }
        catch (OperationCanceledException)
        {
        }

        await orchestrator.AbortAsync(CancellationToken.None);
    }

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

    private sealed class RecordingHandler(List<string> invocations) : IWorkHandler<string>
    {
        public ValueTask HandleAsync(string work, CancellationToken ct)
        {
            invocations.Add(work);
            return ValueTask.CompletedTask;
        }
    }
}
