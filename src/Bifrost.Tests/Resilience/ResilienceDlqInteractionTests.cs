// =============================================================================
// <copyright file="ResilienceDlqInteractionTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.DependencyInjection;
using Bifrost.Resilience;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using TUnit.Core;

namespace Bifrost.Tests.Resilience;

/// <summary>
/// Tests documenting the interaction between Resilience and DLQ retry layers.
/// Resilience wraps enqueue operations; DLQ wraps handler execution.
/// They are independent layers operating on different phases of the work lifecycle.
/// </summary>
[Property("Category", "Unit")]
public class ResilienceDlqInteractionTests
{
    /// <summary>
    /// Verifies that resilience and DLQ operate as independent layers:
    /// resilience wraps enqueue (orchestrator decorator), DLQ wraps handler execution
    /// (handler decorator). They do not compound.
    /// </summary>
    [Test]
    public async Task Resilience_WrapsEnqueue_DLQ_WrapsHandler_IndependentLayers()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var handlerCallCount = 0;
        var failingHandler = Substitute.For<IWorkHandler<string>>();
        failingHandler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                handlerCallCount++;
                return new ValueTask(Task.FromException(new InvalidOperationException("handler failure")));
            });
        services.AddSingleton(failingHandler);

        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);
        builder
            .WithResilience(settings =>
            {
                settings.RetryCount = 2;
                settings.RetryIntervalSeconds = 0;
                settings.UseExponentialBackoff = false;
                settings.TimeoutIntervalSeconds = 30;
            })
            .WithDeadLetterQueue(opts => opts.MaxRetries = 1);

        builder.Build();
        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        var dlq = provider.GetRequiredService<IDeadLetterQueue<string>>();

        // Act - enqueue should succeed (resilience wraps enqueue, not handler)
        await orchestrator.EnqueueAsync("test-work").ConfigureAwait(false);

        // Wait for the handler to process and dead-letter
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (dlq.Count == 0 && !cts.IsCancellationRequested)
        {
            await Task.Delay(50, cts.Token).ConfigureAwait(false);
        }

        // Assert
        // DLQ with MaxRetries=1 means 2 handler attempts (1 initial + 1 retry)
        // Resilience does NOT retry handler failures - it only wraps enqueue
        await Assert.That(handlerCallCount).IsEqualTo(2);
        await Assert.That(dlq.Count).IsEqualTo(1);

        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that handler exceptions do NOT trigger the resilience policy.
    /// Resilience only wraps enqueue operations (EnqueueAsync), not handler execution.
    /// </summary>
    [Test]
    public async Task Resilience_DoesNotRetryHandlerFailures()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var handlerCallCount = 0;
        var failingHandler = Substitute.For<IWorkHandler<string>>();
        failingHandler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                handlerCallCount++;
                return new ValueTask(Task.FromException(new InvalidOperationException("handler failure")));
            });
        services.AddSingleton(failingHandler);

        var builder = services.AddWorkOrchestrator<string>(opts => opts.WorkerCount = 1);
        builder
            .WithResilience(settings =>
            {
                settings.RetryCount = 5; // High retry count
                settings.RetryIntervalSeconds = 0;
                settings.UseExponentialBackoff = false;
                settings.TimeoutIntervalSeconds = 30;
            })
            .WithDeadLetterQueue(opts => opts.MaxRetries = 0); // No DLQ retries

        builder.Build();
        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        var dlq = provider.GetRequiredService<IDeadLetterQueue<string>>();

        // Act
        await orchestrator.EnqueueAsync("test-work").ConfigureAwait(false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (dlq.Count == 0 && !cts.IsCancellationRequested)
        {
            await Task.Delay(50, cts.Token).ConfigureAwait(false);
        }

        // Assert - handler called exactly once (MaxRetries=0 means 1 attempt)
        // Resilience retry count (5) does NOT apply to handler failures
        await Assert.That(handlerCallCount).IsEqualTo(1);
        await Assert.That(dlq.Count).IsEqualTo(1);

        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that enqueue failures do NOT trigger DLQ retry: the DLQ retry
    /// machinery wraps handler execution only. Under rejection routing (T23,
    /// DR-6) the rejected enqueue IS recorded in the dead-letter pathway as
    /// observability — a single entry with <c>AttemptCount = 0</c> and a
    /// <see cref="WorkRejectedException"/> marker, proof that the configured
    /// <c>MaxRetries</c> never engaged (never admitted, never attempted,
    /// never retried) — and the caller still receives the rejection.
    /// </summary>
    [Test]
    public async Task DLQ_DoesNotRetryEnqueueFailures()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Use a handler that blocks forever so the channel stays full
        var blockingHandler = Substitute.For<IWorkHandler<string>>();
        blockingHandler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask(Task.Delay(Timeout.Infinite)));
        services.AddSingleton(blockingHandler);

        // Use capacity=1 to quickly fill the channel
        var builder = services.AddWorkOrchestrator<string>(opts =>
        {
            opts.WorkerCount = 1;
            opts.Capacity = 1;
        });
        builder.WithDeadLetterQueue(opts => opts.MaxRetries = 3);
        builder.Build();
        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        var dlq = provider.GetRequiredService<IDeadLetterQueue<string>>();

        // Fill the channel: first item goes to handler (blocks), second fills the channel
        await orchestrator.EnqueueAsync("item-in-handler").ConfigureAwait(false);

        // Wait briefly for the worker to pick up the first item
        await Task.Delay(100).ConfigureAwait(false);
        orchestrator.TryEnqueue("fill-channel");

        // Act - try to enqueue when full. This is an enqueue-level failure:
        // the DLQ retry machinery must NOT engage (it wraps handler execution
        // only), but rejection routing (DR-6) records the shed item as a
        // dead-letter entry for observability.
        var enqueueResult = orchestrator.TryEnqueue("overflow-item");

        // Assert — the caller still receives the rejection, and the routed
        // entry proves no retry happened: AttemptCount 0 (never admitted,
        // never attempted) with the rejection marker, despite MaxRetries = 3.
        await Assert.That(enqueueResult).IsFalse();
        await Assert.That(dlq.Count).IsEqualTo(1);

        var entries = new List<DeadLetteredWork<string>>();
        await foreach (var entry in dlq.ReadAllAsync().ConfigureAwait(false))
        {
            entries.Add(entry);
        }

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].Work).IsEqualTo("overflow-item");
        await Assert.That(entries[0].AttemptCount).IsEqualTo(0);
        await Assert.That(entries[0].Exception is WorkRejectedException).IsTrue();

        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }
}
