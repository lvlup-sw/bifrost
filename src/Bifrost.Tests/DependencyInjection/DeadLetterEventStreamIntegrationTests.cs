// =============================================================================
// <copyright file="DeadLetterEventStreamIntegrationTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;

using Bifrost.Core;
using Bifrost.Core.Events;
using Bifrost.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using TUnit.Core;

namespace Bifrost.Tests.DependencyInjection;

/// <summary>
/// Integration tests verifying that WorkDeadLetteredEvent is published through the event stream
/// when both WithEventStream() and WithDeadLetterQueue() are configured.
/// </summary>
[Property("Category", "Integration")]
public class DeadLetterEventStreamIntegrationTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies that when both WithEventStream and WithDeadLetterQueue are configured,
    /// a dead-lettered work item produces a WorkDeadLetteredEvent visible in the event stream.
    /// </summary>
    /// <remarks>
    /// The test calls <c>GetEventStreamAsync</c> before the enqueue, so the subscriber is registered
    /// before any event is published, and waits for the first dead-letter event. With
    /// <c>MaxRetries = 0</c> the item gets one attempt, so <c>AttemptCount</c> is 1.
    /// </remarks>
    [Test]
    public async Task WithEventStream_AndWithDeadLetterQueue_PublishesWorkDeadLetteredEvent()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var handler = Substitute.For<IWorkHandler<string>>();
        handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(Task.FromException(new InvalidOperationException("always fails"))));
        services.AddSingleton(handler);

        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);
        builder.WithEventStream();
        builder.WithDeadLetterQueue(opts => opts.MaxRetries = 0);
        builder.Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        var eventOrchestrator = orchestrator as IEventStreamOrchestrator<string>;

        await Assert.That(eventOrchestrator).IsNotNull();

        var deadLetterEvents = new ConcurrentBag<WorkDeadLetteredEvent<string>>();
        using var cts = new CancellationTokenSource(WaitTimeout);
        var stream = eventOrchestrator!.GetEventStreamAsync<WorkDeadLetteredEvent<string>>(cancellationToken: cts.Token);
        var eventTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in stream)
                {
                    deadLetterEvents.Add(evt);
                    break;
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        await orchestrator.EnqueueAsync("doomed-work");

        await eventTask.WaitAsync(WaitTimeout);

        await Assert.That(deadLetterEvents.Count).IsGreaterThanOrEqualTo(1);

        var dlEvent = deadLetterEvents.First();
        await Assert.That(dlEvent.Work).IsEqualTo("doomed-work");
        await Assert.That(dlEvent.AttemptCount).IsEqualTo(1);
        await Assert.That(dlEvent.Exception).IsNotNull();

        await cts.CancelAsync();
        await orchestrator.DisposeAsync();
        await eventTask;
    }

    /// <summary>
    /// Verifies that calling WithDeadLetterQueue before WithEventStream produces the same
    /// behavior (order independence via late-bound builder property).
    /// </summary>
    [Test]
    public async Task WithDeadLetterQueue_AndWithEventStream_OrderIndependent()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var handler = Substitute.For<IWorkHandler<string>>();
        handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(Task.FromException(new InvalidOperationException("always fails"))));
        services.AddSingleton(handler);

        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);
        builder.WithDeadLetterQueue(opts => opts.MaxRetries = 0);
        builder.WithEventStream();
        builder.Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        var eventOrchestrator = orchestrator as IEventStreamOrchestrator<string>;

        await Assert.That(eventOrchestrator).IsNotNull();

        var deadLetterEvents = new ConcurrentBag<WorkDeadLetteredEvent<string>>();
        using var cts = new CancellationTokenSource(WaitTimeout);
        var stream = eventOrchestrator!.GetEventStreamAsync<WorkDeadLetteredEvent<string>>(cancellationToken: cts.Token);
        var eventTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in stream)
                {
                    deadLetterEvents.Add(evt);
                    break;
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        await orchestrator.EnqueueAsync("doomed-work");
        await eventTask.WaitAsync(WaitTimeout);

        await Assert.That(deadLetterEvents.Count).IsGreaterThanOrEqualTo(1);

        var dlEvent = deadLetterEvents.First();
        await Assert.That(dlEvent.Work).IsEqualTo("doomed-work");

        await cts.CancelAsync();
        await orchestrator.DisposeAsync();
        await eventTask;
    }

    /// <summary>
    /// Verifies that when only WithDeadLetterQueue is configured (no event stream),
    /// the DLQ works normally without any publish callback issues.
    /// </summary>
    [Test]
    public async Task WithDeadLetterQueue_WithoutEventStream_NoPublishCallback()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var handler = Substitute.For<IWorkHandler<string>>();
        handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(Task.FromException(new InvalidOperationException("always fails"))));
        services.AddSingleton(handler);

        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);
        builder.WithDeadLetterQueue(opts => opts.MaxRetries = 0);
        builder.Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        await orchestrator.EnqueueAsync("doomed-work");
        await Task.Delay(500);

        await Assert.That(orchestrator is IEventStreamOrchestrator<string>).IsFalse();

        var dlq = provider.GetRequiredService<Bifrost.Core.DeadLetter.IDeadLetterQueue<string>>();
        await Assert.That(dlq.Count).IsGreaterThanOrEqualTo(1);

        await orchestrator.DisposeAsync();
    }

    /// <summary>
    /// Verifies that when only WithEventStream is configured (no DLQ),
    /// the event stream works normally without errors.
    /// </summary>
    /// <remarks>
    /// The test reads events until the item's <c>WorkCompletedEvent</c>, so it needs no fixed wait.
    /// </remarks>
    [Test]
    public async Task WithEventStream_WithoutDeadLetterQueue_NoError()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var handler = Substitute.For<IWorkHandler<string>>();
        handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);
        services.AddSingleton(handler);

        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);
        builder.WithEventStream();
        builder.Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        var eventOrchestrator = orchestrator as IEventStreamOrchestrator<string>;

        await Assert.That(eventOrchestrator).IsNotNull();

        var events = new ConcurrentBag<IOrchestratorEvent>();
        using var cts = new CancellationTokenSource(WaitTimeout);
        var stream = eventOrchestrator!.GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token);
        var eventTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in stream)
                {
                    events.Add(evt);
                    if (evt is WorkCompletedEvent<string>)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        await orchestrator.EnqueueAsync("normal-work");
        await eventTask.WaitAsync(WaitTimeout);

        await Assert.That(events.Count).IsGreaterThan(0);
        var deadLetterEvents = events.Where(e => e is WorkDeadLetteredEvent<string>).ToList();
        await Assert.That(deadLetterEvents.Count).IsEqualTo(0);

        await cts.CancelAsync();
        await orchestrator.DisposeAsync();
        await eventTask;
    }
}
