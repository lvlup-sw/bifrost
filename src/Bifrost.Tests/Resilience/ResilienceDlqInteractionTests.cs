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
    /// <remarks>
    /// The enqueue succeeds, because resilience wraps the enqueue and not the handler. The DLQ has
    /// <c>MaxRetries = 1</c>, which gives two handler attempts (one initial and one retry). Resilience
    /// does not retry handler failures.
    /// </remarks>
    [Test]
    public async Task Resilience_WrapsEnqueue_DLQ_WrapsHandler_IndependentLayers()
    {
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

        await orchestrator.EnqueueAsync("test-work");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (dlq.Count == 0 && !cts.IsCancellationRequested)
        {
            await Task.Delay(50, cts.Token);
        }

        await Assert.That(handlerCallCount).IsEqualTo(2);
        await Assert.That(dlq.Count).IsEqualTo(1);

        await orchestrator.DisposeAsync();
    }

    /// <summary>
    /// Verifies that handler exceptions do NOT trigger the resilience policy.
    /// Resilience only wraps enqueue operations (EnqueueAsync), not handler execution.
    /// </summary>
    /// <remarks>
    /// The resilience retry count is high (5), but the DLQ has <c>MaxRetries = 0</c>, which gives one
    /// attempt. The handler runs exactly once, because the resilience retry count does not apply to
    /// handler failures.
    /// </remarks>
    [Test]
    public async Task Resilience_DoesNotRetryHandlerFailures()
    {
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
                settings.RetryCount = 5;
                settings.RetryIntervalSeconds = 0;
                settings.UseExponentialBackoff = false;
                settings.TimeoutIntervalSeconds = 30;
            })
            .WithDeadLetterQueue(opts => opts.MaxRetries = 0);

        builder.Build();
        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        var dlq = provider.GetRequiredService<IDeadLetterQueue<string>>();

        await orchestrator.EnqueueAsync("test-work");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (dlq.Count == 0 && !cts.IsCancellationRequested)
        {
            await Task.Delay(50, cts.Token);
        }

        await Assert.That(handlerCallCount).IsEqualTo(1);
        await Assert.That(dlq.Count).IsEqualTo(1);

        await orchestrator.DisposeAsync();
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
    /// <remarks>
    /// <para>
    /// The handler blocks forever, so the channel stays full. Capacity 1 fills the channel quickly: the
    /// first item goes to the handler and blocks, and the second item fills the channel. Queue capacity
    /// is available only after the handler has actually started, so the test waits for
    /// <c>handlerEntered</c>.
    /// </para>
    /// <para>
    /// The overflow enqueue is an enqueue-level failure. The DLQ retry machinery must not engage, because
    /// it wraps handler execution only. Rejection routing (DR-6) records the shed item as a dead-letter
    /// entry for observability. The caller still receives the rejection. The routed entry shows that no
    /// retry happened: <c>AttemptCount</c> is 0 (never admitted, never attempted) with the rejection
    /// marker, although <c>MaxRetries = 3</c>.
    /// </para>
    /// </remarks>
    [Test]
    public async Task DLQ_DoesNotRetryEnqueueFailures()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingHandler = Substitute.For<IWorkHandler<string>>();
        blockingHandler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                handlerEntered.TrySetResult();
                return new ValueTask(Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>()));
            });
        services.AddSingleton(blockingHandler);

        var builder = services.AddWorkOrchestrator<string>(opts =>
        {
            opts.WorkerCount = 1;
            opts.Capacity = 1;
        });
        builder.WithDeadLetterQueue(opts => opts.MaxRetries = 3);
        builder.Build();
        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        var dlq = provider.GetRequiredService<IDeadLetterQueue<string>>();

        await orchestrator.EnqueueAsync("item-in-handler");

        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(orchestrator.TryEnqueue("fill-channel")).IsTrue();

        var enqueueResult = orchestrator.TryEnqueue("overflow-item");

        await Assert.That(enqueueResult).IsFalse();
        await Assert.That(dlq.Count).IsEqualTo(1);

        var entries = new List<DeadLetteredWork<string>>();
        await foreach (var entry in dlq.ReadAllAsync())
        {
            entries.Add(entry);
        }

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].Work).IsEqualTo("overflow-item");
        await Assert.That(entries[0].AttemptCount).IsEqualTo(0);
        await Assert.That(entries[0].Exception is WorkRejectedException).IsTrue();

        await orchestrator.DisposeAsync();
    }
}
