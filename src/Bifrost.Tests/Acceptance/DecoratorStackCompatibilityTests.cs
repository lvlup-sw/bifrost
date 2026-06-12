// =============================================================================
// <copyright file="DecoratorStackCompatibilityTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;
using System.Diagnostics;

using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.Core.Events;
using Bifrost.DependencyInjection;
using Bifrost.HealthChecks;
using Bifrost.OpenTelemetry;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Bifrost.Tests.Acceptance;

/// <summary>
/// Decorator-stack compatibility matrix for the issue #17 acceptance criterion
/// (T27, DR-4): "Decorator stack (autoscaling, DLQ, health, OTel) keeps working —
/// depth via the structure's Count." The existing decorator suites exercise the
/// default FIFO binding; this matrix re-exercises the cross-cutting behaviors
/// (round-trip processing, depth observability, dead-letter routing for handler
/// failures AND admission rejections, event streaming, health checks, and
/// dynamically created workers) under all three <see cref="DispatchStrategy"/>
/// bindings through the public DI builder surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Depth assertions are quiescent-only:</b> the priority bindings document
/// <c>Count</c> as exact only when the structure is quiescent (no concurrent
/// producers/consumers), so every <see cref="IWorkOrchestrator{TWork}.PendingCount"/>
/// assertion here parks the single worker behind a gate first — mid-flight counts
/// on priority bindings are never asserted exactly.
/// </para>
/// <para>
/// <b>Admission asymmetry by design (DR-6):</b> the priority strategies are
/// fail-fast at admission (capacity/watermark overflow maps to
/// Rejected(CapacityExceeded) and dead-letters with the <c>AttemptCount = 0</c> +
/// <see cref="WorkRejectedException"/> marker), while the FIFO default keeps
/// producer-wait semantics — the overflow enqueue waits for space and never
/// produces an admission entry. The matrix asserts both sides of that asymmetry.
/// </para>
/// </remarks>
[Property("Category", "Acceptance")]
public sealed class DecoratorStackCompatibilityTests
{
    /// <summary>
    /// Per-step ceiling for operations expected to complete promptly.
    /// </summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Acceptance (issue #17, all strategies): the full decorator stack — DLQ +
    /// OpenTelemetry + autoscaling — composes through the DI builder and round-trips
    /// work end-to-end: every mixed-class enqueue is Accepted, every item is
    /// processed exactly once, and nothing touches the dead-letter pathway.
    /// </summary>
    /// <param name="strategy">The dispatch strategy binding under test.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    [Arguments(DispatchStrategy.Fifo)]
    [Arguments(DispatchStrategy.PriorityMultiQueue)]
    [Arguments(DispatchStrategy.PriorityLocking)]
    public async Task FullStack_RoundTrip_WorkProcessed(DispatchStrategy strategy)
    {
        // Arrange — full stack via the builder; capacity far above the backlog so
        // no admission watermark sheds (0.90 × 64 = 57 ≫ 12).
        var handler = new MatrixHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkHandler<string>>(handler);
        services.AddWorkOrchestrator<string>(opts =>
        {
            opts.Capacity = 64;
            opts.WorkerCount = 2;
            opts.DispatchStrategy = strategy;
        })
        .WithDeadLetterQueue()
        .WithOpenTelemetry()
        .WithAutoscaling()
        .Build();

        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        // Act — enqueue a mixed-class backlog through the decorated surface.
        const int itemCount = 12;
        for (var i = 0; i < itemCount; i++)
        {
            var result = await orchestrator
                .EnqueueAsync($"item-{i:D2}", (WorkClass)(i % 3))
                .ConfigureAwait(false);
            await Assert.That(result).IsEqualTo(EnqueueResult.Accepted);
        }

        using var drainCts = new CancellationTokenSource(WaitTimeout);
        await orchestrator.DrainAsync(drainCts.Token).ConfigureAwait(false);

        // Assert — every item processed exactly once (no loss, no duplication).
        var processed = handler.ProcessedSnapshot();
        await Assert.That(processed.Count).IsEqualTo(itemCount);
        await Assert.That(processed.Distinct().Count()).IsEqualTo(itemCount);

        // Assert — the dead-letter pathway stayed untouched (no failures, no
        // rejections anywhere in the stack).
        var dlq = provider.GetRequiredService<IDeadLetterQueue<string>>();
        await Assert.That(dlq.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Acceptance (issue #17, all strategies): queue depth is observable through
    /// the full decorator stack via <see cref="IWorkOrchestrator{TWork}.PendingCount"/>
    /// — "depth via the structure's Count". Asserted only at quiescence (single
    /// worker parked behind a gate), where all three bindings document exactness;
    /// mid-flight counts on the priority bindings are approximate by contract and
    /// are deliberately never asserted here.
    /// </summary>
    /// <param name="strategy">The dispatch strategy binding under test.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    [Arguments(DispatchStrategy.Fifo)]
    [Arguments(DispatchStrategy.PriorityMultiQueue)]
    [Arguments(DispatchStrategy.PriorityLocking)]
    public async Task PendingCount_ApproximateTolerant_AllStrategies(DispatchStrategy strategy)
    {
        // Arrange — full stack, single worker so one gate item parks ALL consumers.
        var handler = new MatrixHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkHandler<string>>(handler);
        services.AddWorkOrchestrator<string>(opts =>
        {
            opts.Capacity = 64;
            opts.WorkerCount = 1;
            opts.DispatchStrategy = strategy;
        })
        .WithDeadLetterQueue()
        .WithOpenTelemetry()
        .WithAutoscaling()
        .Build();

        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        // Park the single worker: the gate item is dequeued (so it never counts as
        // pending) and the queue is quiescent from here on.
        var gateResult = await orchestrator.EnqueueAsync("gate").ConfigureAwait(false);
        await Assert.That(gateResult).IsEqualTo(EnqueueResult.Accepted);
        await handler.GateEntered.WaitAsync(WaitTimeout).ConfigureAwait(false);

        // Act — a mixed-class backlog behind the parked worker (10 ≪ the 0.90 × 64
        // Batch watermark, so every class admits).
        const int depth = 10;
        for (var i = 0; i < depth; i++)
        {
            var result = await orchestrator
                .EnqueueAsync($"item-{i:D2}", (WorkClass)(i % 3))
                .ConfigureAwait(false);
            await Assert.That(result).IsEqualTo(EnqueueResult.Accepted);
        }

        // Assert — quiescent depth is exact through every decorator layer.
        await Assert.That(orchestrator.PendingCount).IsEqualTo(depth);

        // Release and drain — depth returns to zero (also exact at quiescence).
        handler.ReleaseGate();
        using var drainCts = new CancellationTokenSource(WaitTimeout);
        await orchestrator.DrainAsync(drainCts.Token).ConfigureAwait(false);
        await Assert.That(orchestrator.PendingCount).IsEqualTo(0);
    }

    /// <summary>
    /// Acceptance (issue #17, all strategies): a handler that throws routes the
    /// item through the existing DLQ failure pathway identically under each
    /// strategy — retries exhausted (<c>AttemptCount = 1 + MaxRetries</c>), the
    /// handler's own exception preserved, and healthy items unaffected.
    /// </summary>
    /// <param name="strategy">The dispatch strategy binding under test.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    [Arguments(DispatchStrategy.Fifo)]
    [Arguments(DispatchStrategy.PriorityMultiQueue)]
    [Arguments(DispatchStrategy.PriorityLocking)]
    public async Task DlqHandlerFailure_RoutesPerStrategy(DispatchStrategy strategy)
    {
        // Arrange — DLQ with one retry: the poison item fails twice, then
        // dead-letters with AttemptCount 2.
        var handler = new MatrixHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkHandler<string>>(handler);
        services.AddWorkOrchestrator<string>(opts =>
        {
            opts.Capacity = 16;
            opts.WorkerCount = 1;
            opts.DispatchStrategy = strategy;
        })
        .WithDeadLetterQueue(dlqOpts => dlqOpts.MaxRetries = 1)
        .Build();

        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        // Act — a poison item between two healthy ones.
        var first = await orchestrator.EnqueueAsync("ok-1").ConfigureAwait(false);
        var poison = await orchestrator.EnqueueAsync("poison-item").ConfigureAwait(false);
        var second = await orchestrator.EnqueueAsync("ok-2").ConfigureAwait(false);
        await Assert.That(first).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(poison).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(second).IsEqualTo(EnqueueResult.Accepted);

        using var drainCts = new CancellationTokenSource(WaitTimeout);
        await orchestrator.DrainAsync(drainCts.Token).ConfigureAwait(false);

        // Assert — exactly the poison item dead-lettered, with the handler's own
        // exception and the exhausted attempt count (1 initial + 1 retry).
        var dlq = provider.GetRequiredService<IDeadLetterQueue<string>>();
        var entries = await ReadAllEntriesAsync(dlq).ConfigureAwait(false);
        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].Work).IsEqualTo("poison-item");
        await Assert.That(entries[0].AttemptCount).IsEqualTo(2);
        await Assert.That(entries[0].Exception is InvalidOperationException).IsTrue();

        // Assert — the healthy items processed normally around the failure.
        var processed = handler.ProcessedSnapshot();
        await Assert.That(processed.Count).IsEqualTo(2);
        await Assert.That(processed).Contains("ok-1");
        await Assert.That(processed).Contains("ok-2");
    }

    /// <summary>
    /// Acceptance (issue #17, priority strategies): at capacity the fail-fast
    /// admission rejects the overflow enqueue, and the rejection dead-letters
    /// through the DLQ stack with the rejection-distinguishing marker —
    /// <c>AttemptCount = 0</c> (never admitted, never attempted) and a
    /// <see cref="WorkRejectedException"/> carrying
    /// <see cref="RejectionReason.CapacityExceeded"/> — while the caller still
    /// receives the typed rejection (routing is observability, not retry).
    /// </summary>
    /// <param name="strategy">The priority binding under test.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    [Arguments(DispatchStrategy.PriorityMultiQueue)]
    [Arguments(DispatchStrategy.PriorityLocking)]
    public async Task DlqAdmissionRejection_RoutesOnPriorityStrategies(DispatchStrategy strategy)
    {
        // Arrange — capacity 2, single worker parked: the queue state is
        // deterministic while filling to hard capacity (Interactive watermark 1.0).
        var handler = new MatrixHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkHandler<string>>(handler);
        services.AddWorkOrchestrator<string>(opts =>
        {
            opts.Capacity = 2;
            opts.WorkerCount = 1;
            opts.DispatchStrategy = strategy;
        })
        .WithDeadLetterQueue()
        .Build();

        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        var dlq = provider.GetRequiredService<IDeadLetterQueue<string>>();

        // Act — park the worker, fill to capacity, then shed one.
        var parked = await orchestrator.EnqueueAsync("gate", WorkClass.Interactive).ConfigureAwait(false);
        await Assert.That(parked).IsEqualTo(EnqueueResult.Accepted);
        await handler.GateEntered.WaitAsync(WaitTimeout).ConfigureAwait(false);

        var first = await orchestrator.EnqueueAsync("keep-1", WorkClass.Interactive).ConfigureAwait(false);
        var second = await orchestrator.EnqueueAsync("keep-2", WorkClass.Interactive).ConfigureAwait(false);
        var shed = await orchestrator.EnqueueAsync("shed-item", WorkClass.Interactive).ConfigureAwait(false);

        // Assert — the caller still receives the typed rejection (no swallowing).
        await Assert.That(first).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(second).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(shed.IsAccepted).IsFalse();
        await Assert.That(shed.Reason).IsEqualTo(RejectionReason.CapacityExceeded);

        // Assert — the shed item is in the dead-letter pathway with the
        // rejection-distinguishing marker.
        var entries = await ReadAllEntriesAsync(dlq).ConfigureAwait(false);
        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].Work).IsEqualTo("shed-item");
        await Assert.That(entries[0].AttemptCount).IsEqualTo(0);
        var marker = entries[0].Exception as WorkRejectedException;
        await Assert.That(marker).IsNotNull();
        await Assert.That(marker!.Reason).IsEqualTo(RejectionReason.CapacityExceeded);

        // Cleanup — release the parked worker so disposal does not wait.
        handler.ReleaseGate();
    }

    /// <summary>
    /// Acceptance (issue #17, FIFO negative case): the FIFO default keeps
    /// producer-wait semantics at capacity — the overflow enqueue WAITS for space
    /// instead of rejecting, eventually completes Accepted, and the admission
    /// pathway never produces a dead-letter entry.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DlqAdmissionAtCapacity_Fifo_WaitsWithoutDeadLettering()
    {
        // Arrange — capacity 2, single worker parked, DLQ configured: if FIFO ever
        // produced admission entries, this stack would capture them.
        var handler = new MatrixHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkHandler<string>>(handler);
        services.AddWorkOrchestrator<string>(opts =>
        {
            opts.Capacity = 2;
            opts.WorkerCount = 1;
            opts.DispatchStrategy = DispatchStrategy.Fifo;
        })
        .WithDeadLetterQueue()
        .Build();

        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        var dlq = provider.GetRequiredService<IDeadLetterQueue<string>>();

        // Park the worker and fill to capacity.
        var parked = await orchestrator.EnqueueAsync("gate").ConfigureAwait(false);
        await Assert.That(parked).IsEqualTo(EnqueueResult.Accepted);
        await handler.GateEntered.WaitAsync(WaitTimeout).ConfigureAwait(false);

        var first = await orchestrator.EnqueueAsync("fill-1").ConfigureAwait(false);
        var second = await orchestrator.EnqueueAsync("fill-2").ConfigureAwait(false);
        await Assert.That(first).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(second).IsEqualTo(EnqueueResult.Accepted);

        // Act — the overflow enqueue must WAIT (producer-wait), not fail fast: with
        // the queue full and the only consumer parked, it cannot have completed.
        var overflow = orchestrator.EnqueueAsync("overflow");
        await Assert.That(overflow.IsCompleted).IsFalse();

        // Assert — no admission entry exists while the producer is waiting.
        await Assert.That(dlq.Count).IsEqualTo(0);

        // Release the worker: space frees, the wait resolves to acceptance.
        handler.ReleaseGate();
        var overflowResult = await overflow.ConfigureAwait(false);
        await Assert.That(overflowResult).IsEqualTo(EnqueueResult.Accepted);

        using var drainCts = new CancellationTokenSource(WaitTimeout);
        await orchestrator.DrainAsync(drainCts.Token).ConfigureAwait(false);

        // Assert — everything processed, and the FIFO admission pathway never
        // dead-lettered anything.
        await Assert.That(handler.ProcessedSnapshot().Count).IsEqualTo(4);
        await Assert.That(dlq.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Acceptance (issue #17, all strategies): work-processed events flow through
    /// the event-stream decorator under each strategy, and
    /// <see cref="WorkEnqueuedEvent{TWork}"/> publishes ONLY for accepted enqueues —
    /// a rejected/declined admission never produces an enqueued event.
    /// </summary>
    /// <param name="strategy">The dispatch strategy binding under test.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    [Arguments(DispatchStrategy.Fifo)]
    [Arguments(DispatchStrategy.PriorityMultiQueue)]
    [Arguments(DispatchStrategy.PriorityLocking)]
    public async Task EventStream_PublishesPerStrategy(DispatchStrategy strategy)
    {
        // Arrange — event stream only (outermost decorator), capacity 4 so the
        // declined-admission leg is reachable on every strategy via TryEnqueue.
        var handler = new MatrixHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkHandler<string>>(handler);
        services.AddWorkOrchestrator<string>(opts =>
        {
            opts.Capacity = 4;
            opts.WorkerCount = 1;
            opts.DispatchStrategy = strategy;
        })
        .WithEventStream()
        .Build();

        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        var eventStream = orchestrator as IEventStreamOrchestrator<string>;
        await Assert.That(eventStream).IsNotNull();

        // Subscribe BEFORE acting — GetEventStreamAsync registers the subscriber
        // channel eagerly at call time, so no event published after these calls can
        // be missed; DrainAsync completes the channels, ending the collectors.
        var enqueuedWorks = new ConcurrentQueue<string>();
        var completedWorks = new ConcurrentQueue<(string Work, bool Success)>();
        var enqueuedStream = eventStream!.GetEventStreamAsync<WorkEnqueuedEvent<string>>();
        var completedStream = eventStream.GetEventStreamAsync<WorkCompletedEvent<string>>();
        var enqueuedCollector = Task.Run(async () =>
        {
            await foreach (var evt in enqueuedStream.ConfigureAwait(false))
            {
                enqueuedWorks.Enqueue(evt.Work);
            }
        });
        var completedCollector = Task.Run(async () =>
        {
            await foreach (var evt in completedStream.ConfigureAwait(false))
            {
                completedWorks.Enqueue((evt.Work, evt.Success));
            }
        });

        // Act — park the worker, fill to hard capacity (Interactive watermark 1.0),
        // then attempt one over-capacity TryEnqueue: declined on every strategy
        // (FIFO: full channel; priority: fail-fast admission).
        var parked = await orchestrator.EnqueueAsync("gate", WorkClass.Interactive).ConfigureAwait(false);
        await Assert.That(parked).IsEqualTo(EnqueueResult.Accepted);
        await handler.GateEntered.WaitAsync(WaitTimeout).ConfigureAwait(false);

        for (var i = 0; i < 4; i++)
        {
            var accepted = await orchestrator
                .EnqueueAsync($"fill-{i}", WorkClass.Interactive)
                .ConfigureAwait(false);
            await Assert.That(accepted).IsEqualTo(EnqueueResult.Accepted);
        }

        var overflowAdmitted = orchestrator.TryEnqueue("overflow", WorkClass.Interactive);
        await Assert.That(overflowAdmitted).IsFalse();

        // Drain — processes the backlog, then completes the subscriber channels.
        handler.ReleaseGate();
        using var drainCts = new CancellationTokenSource(WaitTimeout);
        await orchestrator.DrainAsync(drainCts.Token).ConfigureAwait(false);
        await Task.WhenAll(enqueuedCollector, completedCollector).WaitAsync(WaitTimeout).ConfigureAwait(false);

        // Assert — enqueued events for exactly the five accepted items, never for
        // the declined overflow.
        var enqueued = enqueuedWorks.ToList();
        await Assert.That(enqueued.Count).IsEqualTo(5);
        await Assert.That(enqueued.Contains("overflow")).IsFalse();
        await Assert.That(enqueued.Contains("gate")).IsTrue();

        // Assert — work-processed events flowed for all five accepted items.
        var completed = completedWorks.ToList();
        await Assert.That(completed.Count).IsEqualTo(5);
        await Assert.That(completed.All(c => c.Success)).IsTrue();
        await Assert.That(completed.Select(c => c.Work).Contains("gate")).IsTrue();
        for (var i = 0; i < 4; i++)
        {
            await Assert.That(completed.Select(c => c.Work).Contains($"fill-{i}")).IsTrue();
        }
    }

    /// <summary>
    /// Acceptance (issue #17, all strategies): the existing health check
    /// registration composes with the full decorator stack and reports Healthy on
    /// an idle orchestrator under each strategy — the check reads depth and worker
    /// state through the decorated surface.
    /// </summary>
    /// <param name="strategy">The dispatch strategy binding under test.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    [Arguments(DispatchStrategy.Fifo)]
    [Arguments(DispatchStrategy.PriorityMultiQueue)]
    [Arguments(DispatchStrategy.PriorityLocking)]
    public async Task HealthCheck_RegistersAndReportsPerStrategy(DispatchStrategy strategy)
    {
        // Arrange — full stack plus health checks.
        var handler = new MatrixHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkHandler<string>>(handler);
        services.AddWorkOrchestrator<string>(opts =>
        {
            opts.Capacity = 16;
            opts.WorkerCount = 1;
            opts.DispatchStrategy = strategy;
        })
        .WithDeadLetterQueue()
        .WithOpenTelemetry()
        .WithAutoscaling()
        .WithHealthChecks()
        .Build();

        await using var provider = services.BuildServiceProvider();

        // Realize the orchestrator singleton so the check observes the live instance.
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        await Assert.That(orchestrator).IsNotNull();

        // Act
        var healthService = provider.GetRequiredService<HealthCheckService>();
        var report = await healthService.CheckHealthAsync().ConfigureAwait(false);

        // Assert — the default-named check is registered and reports Healthy on the
        // idle (0% utilization) orchestrator, for every binding.
        await Assert.That(report.Entries.ContainsKey("WorkOrchestrator<String>")).IsTrue();
        await Assert.That(report.Entries["WorkOrchestrator<String>"].Status).IsEqualTo(HealthStatus.Healthy);
        await Assert.That(report.Status).IsEqualTo(HealthStatus.Healthy);
    }

    /// <summary>
    /// Acceptance (issue #17, all strategies): dynamically created workers
    /// (<see cref="IWorkOrchestrator{TWork}.CreateWorkerFunction()"/>, the
    /// autoscaling scale-up path) consume from the SAME queue binding as built-in
    /// workers (T17): with the lone built-in worker parked behind a gate, a dynamic
    /// worker alone drains the backlog under each strategy.
    /// </summary>
    /// <param name="strategy">The dispatch strategy binding under test.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    [Arguments(DispatchStrategy.Fifo)]
    [Arguments(DispatchStrategy.PriorityMultiQueue)]
    [Arguments(DispatchStrategy.PriorityLocking)]
    public async Task Autoscaling_DynamicWorkers_ConsumePerStrategy(DispatchStrategy strategy)
    {
        // Arrange — full stack with autoscaling; a single built-in worker that the
        // gate item will park, leaving the backlog exclusively to the dynamic worker.
        var handler = new MatrixHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkHandler<string>>(handler);
        services.AddWorkOrchestrator<string>(opts =>
        {
            opts.Capacity = 64;
            opts.WorkerCount = 1;
            opts.DispatchStrategy = strategy;
        })
        .WithDeadLetterQueue()
        .WithOpenTelemetry()
        .WithAutoscaling()
        .Build();

        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        // Park the built-in worker.
        var parked = await orchestrator.EnqueueAsync("gate").ConfigureAwait(false);
        await Assert.That(parked).IsEqualTo(EnqueueResult.Accepted);
        await handler.GateEntered.WaitAsync(WaitTimeout).ConfigureAwait(false);

        // Backlog that only a dynamic worker can drain.
        const int itemCount = 6;
        for (var i = 0; i < itemCount; i++)
        {
            var result = await orchestrator
                .EnqueueAsync($"item-{i}", (WorkClass)(i % 3))
                .ConfigureAwait(false);
            await Assert.That(result).IsEqualTo(EnqueueResult.Accepted);
        }

        // Act — one dynamically created worker through the decorated surface (the
        // autoscaling decorator wraps it with metrics instrumentation).
        using var workerCts = new CancellationTokenSource();
        var workerFunc = orchestrator.CreateWorkerFunction();
        var workerTask = Task.Run(() => workerFunc("dynamic-worker", workerCts.Token));

        // Assert — the dynamic worker alone consumed the entire backlog while the
        // built-in worker stayed parked (the gate item never completed).
        await handler.WaitForProcessedCountAsync(itemCount, WaitTimeout).ConfigureAwait(false);
        var processed = handler.ProcessedSnapshot();
        await Assert.That(processed.Count).IsEqualTo(itemCount);
        await Assert.That(processed.Contains("gate")).IsFalse();
        for (var i = 0; i < itemCount; i++)
        {
            await Assert.That(processed).Contains($"item-{i}");
        }

        // Cleanup — release the gate, drain, and stop the dynamic worker (it exits
        // naturally once the drained queue completes; cancellation is a backstop).
        handler.ReleaseGate();
        using var drainCts = new CancellationTokenSource(WaitTimeout);
        await orchestrator.DrainAsync(drainCts.Token).ConfigureAwait(false);
        await workerCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await workerTask.WaitAsync(WaitTimeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
    }

    /// <summary>
    /// Reads every currently buffered dead-letter entry. The channel-backed queue's
    /// <c>ReadAllAsync</c> yields what is available and completes — it never blocks
    /// awaiting future entries.
    /// </summary>
    /// <param name="dlq">The dead-letter queue to read.</param>
    /// <returns>The buffered entries in dead-letter order.</returns>
    private static async Task<List<DeadLetteredWork<string>>> ReadAllEntriesAsync(IDeadLetterQueue<string> dlq)
    {
        var entries = new List<DeadLetteredWork<string>>();
        await foreach (var entry in dlq.ReadAllAsync().ConfigureAwait(false))
        {
            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// Shared matrix handler: parks the worker on the "gate" item until released,
    /// throws <see cref="InvalidOperationException"/> on items prefixed "poison"
    /// (exercising the DLQ failure pathway), and records every successfully
    /// completed item.
    /// </summary>
    private sealed class MatrixHandler : IWorkHandler<string>
    {
        private readonly TaskCompletionSource _gateEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _processed = new();

        /// <summary>
        /// Gets a task that completes when a worker has entered the gate item's
        /// handler (a backlog can then be built behind the parked worker).
        /// </summary>
        public Task GateEntered => _gateEntered.Task;

        /// <summary>
        /// Releases the gate, letting the parked worker complete the gate item.
        /// </summary>
        public void ReleaseGate() => _release.TrySetResult();

        /// <summary>
        /// Takes a snapshot of the successfully completed items.
        /// </summary>
        /// <returns>The completed items in completion order.</returns>
        public List<string> ProcessedSnapshot() => [.. _processed];

        /// <summary>
        /// Polls until at least <paramref name="count"/> items have completed or
        /// the timeout elapses; callers assert the resulting state afterwards.
        /// </summary>
        /// <param name="count">The completion count to wait for.</param>
        /// <param name="timeout">The maximum time to wait.</param>
        /// <returns>A task that completes when the count is reached or the timeout elapses.</returns>
        public async Task WaitForProcessedCountAsync(int count, TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            while (_processed.Count < count && stopwatch.Elapsed < timeout)
            {
                await Task.Delay(10).ConfigureAwait(false);
            }
        }

        /// <inheritdoc/>
        public async ValueTask HandleAsync(string work, CancellationToken ct)
        {
            if (work == "gate")
            {
                _gateEntered.TrySetResult();
                await _release.Task.WaitAsync(ct).ConfigureAwait(false);
                _processed.Enqueue(work);
                return;
            }

            if (work.StartsWith("poison", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Poisoned work item: {work}");
            }

            _processed.Enqueue(work);
        }
    }
}
