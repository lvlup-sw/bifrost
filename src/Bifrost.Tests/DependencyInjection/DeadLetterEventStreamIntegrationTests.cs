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
    /// <summary>
    /// Verifies that when both WithEventStream and WithDeadLetterQueue are configured,
    /// a dead-lettered work item produces a WorkDeadLetteredEvent visible in the event stream.
    /// </summary>
    /// <remarks>
    /// The test waits 100 ms so that the subscriber registers before the enqueue. With
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var eventTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in eventOrchestrator!
                    .GetEventStreamAsync<WorkDeadLetteredEvent<string>>(cancellationToken: cts.Token)
                    .ConfigureAwait(false))
                {
                    deadLetterEvents.Add(evt);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        await Task.Delay(100).ConfigureAwait(false);

        await orchestrator.EnqueueAsync("doomed-work").ConfigureAwait(false);

        await Task.Delay(1000).ConfigureAwait(false);

        await Assert.That(deadLetterEvents.Count).IsGreaterThanOrEqualTo(1);

        var dlEvent = deadLetterEvents.First();
        await Assert.That(dlEvent.Work).IsEqualTo("doomed-work");
        await Assert.That(dlEvent.AttemptCount).IsEqualTo(1);
        await Assert.That(dlEvent.Exception).IsNotNull();

        await cts.CancelAsync().ConfigureAwait(false);
        await orchestrator.DisposeAsync().ConfigureAwait(false);
        await eventTask.ConfigureAwait(false);
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var eventTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in eventOrchestrator!
                    .GetEventStreamAsync<WorkDeadLetteredEvent<string>>(cancellationToken: cts.Token)
                    .ConfigureAwait(false))
                {
                    deadLetterEvents.Add(evt);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        await Task.Delay(100).ConfigureAwait(false);

        await orchestrator.EnqueueAsync("doomed-work").ConfigureAwait(false);
        await Task.Delay(1000).ConfigureAwait(false);

        await Assert.That(deadLetterEvents.Count).IsGreaterThanOrEqualTo(1);

        var dlEvent = deadLetterEvents.First();
        await Assert.That(dlEvent.Work).IsEqualTo("doomed-work");

        await cts.CancelAsync().ConfigureAwait(false);
        await orchestrator.DisposeAsync().ConfigureAwait(false);
        await eventTask.ConfigureAwait(false);
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

        await orchestrator.EnqueueAsync("doomed-work").ConfigureAwait(false);
        await Task.Delay(500).ConfigureAwait(false);

        await Assert.That(orchestrator is IEventStreamOrchestrator<string>).IsFalse();

        var dlq = provider.GetRequiredService<Bifrost.Core.DeadLetter.IDeadLetterQueue<string>>();
        await Assert.That(dlq.Count).IsGreaterThanOrEqualTo(1);

        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that when only WithEventStream is configured (no DLQ),
    /// the event stream works normally without errors.
    /// </summary>
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var eventTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in eventOrchestrator!
                    .GetEventStreamAsync<IOrchestratorEvent>(cancellationToken: cts.Token)
                    .ConfigureAwait(false))
                {
                    events.Add(evt);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        await Task.Delay(100).ConfigureAwait(false);

        await orchestrator.EnqueueAsync("normal-work").ConfigureAwait(false);
        await Task.Delay(500).ConfigureAwait(false);

        await Assert.That(events.Count).IsGreaterThan(0);
        var deadLetterEvents = events.Where(e => e is WorkDeadLetteredEvent<string>).ToList();
        await Assert.That(deadLetterEvents.Count).IsEqualTo(0);

        await cts.CancelAsync().ConfigureAwait(false);
        await orchestrator.DisposeAsync().ConfigureAwait(false);
        await eventTask.ConfigureAwait(false);
    }
}
