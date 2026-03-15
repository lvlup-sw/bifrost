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
    /// <summary>
    /// Verifies the full decorator chain processes work end-to-end:
    /// handler called, events published, metrics tracked.
    /// </summary>
    [Test]
    public async Task FullDecoratorChain_ProcessesWorkEndToEnd()
    {
        // Arrange — real handler that records invocations
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

        // Verify decorator chain: AutoscalingOrchestrator (outer) → EventStreamOrchestrator (inner)
        await Assert.That(orchestrator).IsAssignableTo<AutoscalingOrchestrator<string>>();

        // Access the inner EventStreamOrchestrator via reflection (it's the inner decorator)
        var eventStream = GetInnerOrchestrator<IEventStreamOrchestrator<string>>(orchestrator);
        await Assert.That(eventStream).IsNotNull();

        // Subscribe to events BEFORE enqueuing
        var enqueuedEvents = new List<WorkEnqueuedEvent<string>>();
        var completedEvents = new List<WorkCompletedEvent<string>>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var enqueueSubscriber = Task.Run(async () =>
        {
            await foreach (var evt in eventStream!.GetEventStreamAsync<WorkEnqueuedEvent<string>>(cancellationToken: cts.Token).ConfigureAwait(false))
            {
                enqueuedEvents.Add(evt);
                if (enqueuedEvents.Count >= 1) break;
            }
        });

        var completedSubscriber = Task.Run(async () =>
        {
            await foreach (var evt in eventStream!.GetEventStreamAsync<WorkCompletedEvent<string>>(cancellationToken: cts.Token).ConfigureAwait(false))
            {
                completedEvents.Add(evt);
                if (completedEvents.Count >= 1) break;
            }
        });

        // Give subscribers time to start
        await Task.Delay(100).ConfigureAwait(false);

        // Act — start a worker and enqueue work
        var workerFunc = orchestrator.CreateWorkerFunction();
        var workerTask = Task.Run(() => workerFunc("integration-worker", cts.Token));
        await Task.Delay(50).ConfigureAwait(false); // Let worker start reading

        await orchestrator.EnqueueAsync("integration-test-item").ConfigureAwait(false);

        // Wait for subscribers to receive events
        await Task.WhenAll(enqueueSubscriber, completedSubscriber)
            .WaitAsync(TimeSpan.FromSeconds(3))
            .ConfigureAwait(false);

        // Assert — handler was called
        await Assert.That(handlerInvocations).Contains("integration-test-item");

        // Assert — WorkEnqueuedEvent was published
        await Assert.That(enqueuedEvents.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(enqueuedEvents[0].Work).IsEqualTo("integration-test-item");

        // Assert — WorkCompletedEvent was published
        await Assert.That(completedEvents.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(completedEvents[0].Work).IsEqualTo("integration-test-item");
        await Assert.That(completedEvents[0].Success).IsTrue();

        // Assert — metrics service is wired and accessible
        var metrics = provider.GetRequiredService<IWorkerMetrics>();
        await Assert.That(metrics).IsNotNull();

        // Cleanup
        await orchestrator.StopAsync(CancellationToken.None).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
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

            // Try to get the _inner field via reflection
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
