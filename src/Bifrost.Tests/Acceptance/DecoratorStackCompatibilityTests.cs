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
    /// <remarks>
    /// <para>The builder makes the full stack. The capacity is far above the backlog, so no
    /// admission watermark sheds (0.90 × 64 = 57 ≫ 12). The test enqueues a mixed-class backlog
    /// through the decorated surface. Every item must be processed exactly one time (no loss, no
    /// duplication), and the dead-letter pathway must stay untouched (no failures and no rejections
    /// anywhere in the stack).</para>
    /// <para><c>WorkOrchestrator.DisposeAsync</c> owns the cleanup. The test disposes the
    /// orchestrator, not the provider, to prevent the double-dispose path on resources that the
    /// orchestrator owns.</para>
    /// </remarks>
    [Test]
    [Arguments(DispatchStrategy.Fifo)]
    [Arguments(DispatchStrategy.PriorityRelaxed)]
    [Arguments(DispatchStrategy.PriorityExact)]
    public async Task FullStack_RoundTrip_WorkProcessed(DispatchStrategy strategy)
    {
        var handler = new MatrixHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkHandler<string>>(handler);
        services.AddWorkOrchestrator<string>(opts =>
        {
            opts.Capacity = 64;
            opts.WorkerCount = 2;
            opts.DispatchStrategy = strategy;
            opts.FullMode = QueueFullMode.Reject;
        })
        .WithDeadLetterQueue()
        .WithOpenTelemetry()
        .WithAutoscaling()
        .Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        const int itemCount = 12;
        for (var i = 0; i < itemCount; i++)
        {
            var result = await orchestrator.EnqueueAsync($"item-{i:D2}", (WorkClass)(i % 3));
            await Assert.That(result).IsEqualTo(EnqueueResult.Accepted);
        }

        using var drainCts = new CancellationTokenSource(WaitTimeout);
        await orchestrator.DrainAsync(drainCts.Token);

        var processed = handler.ProcessedSnapshot();
        await Assert.That(processed.Count).IsEqualTo(itemCount);
        await Assert.That(processed.Distinct().Count()).IsEqualTo(itemCount);

        var dlq = provider.GetRequiredService<IDeadLetterQueue<string>>();
        await Assert.That(dlq.Count).IsEqualTo(0);

        await orchestrator.DisposeAsync();
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
    /// <remarks>
    /// The full stack has a single worker, so one gate item parks ALL consumers. The worker
    /// dequeues the gate item, so the gate item never counts as pending, and the queue is quiescent
    /// after that point. The mixed-class backlog behind the parked worker is 10 items, much less
    /// than the 0.90 × 64 Batch watermark, so every class admits. The quiescent depth must be exact
    /// through every decorator layer. After the gate releases and the queue drains, the depth
    /// returns to zero, which is also exact at quiescence.
    /// </remarks>
    [Test]
    [Arguments(DispatchStrategy.Fifo)]
    [Arguments(DispatchStrategy.PriorityRelaxed)]
    [Arguments(DispatchStrategy.PriorityExact)]
    public async Task PendingCount_ApproximateTolerant_AllStrategies(DispatchStrategy strategy)
    {
        var handler = new MatrixHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkHandler<string>>(handler);
        services.AddWorkOrchestrator<string>(opts =>
        {
            opts.Capacity = 64;
            opts.WorkerCount = 1;
            opts.DispatchStrategy = strategy;
            opts.FullMode = QueueFullMode.Reject;
        })
        .WithDeadLetterQueue()
        .WithOpenTelemetry()
        .WithAutoscaling()
        .Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        var gateResult = await orchestrator.EnqueueAsync("gate");
        await Assert.That(gateResult).IsEqualTo(EnqueueResult.Accepted);
        await handler.GateEntered.WaitAsync(WaitTimeout);

        const int depth = 10;
        for (var i = 0; i < depth; i++)
        {
            var result = await orchestrator.EnqueueAsync($"item-{i:D2}", (WorkClass)(i % 3));
            await Assert.That(result).IsEqualTo(EnqueueResult.Accepted);
        }

        await Assert.That(orchestrator.PendingCount).IsEqualTo(depth);

        handler.ReleaseGate();
        using var drainCts = new CancellationTokenSource(WaitTimeout);
        await orchestrator.DrainAsync(drainCts.Token);
        await Assert.That(orchestrator.PendingCount).IsEqualTo(0);

        await orchestrator.DisposeAsync();
    }

    /// <summary>
    /// Acceptance (issue #17, all strategies): a handler that throws routes the
    /// item through the existing DLQ failure pathway identically under each
    /// strategy — retries exhausted (<c>AttemptCount = 1 + MaxRetries</c>), the
    /// handler's own exception preserved, and healthy items unaffected.
    /// </summary>
    /// <param name="strategy">The dispatch strategy binding under test.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// The DLQ has one retry, so the poison item fails two times and then dead-letters with
    /// <c>AttemptCount</c> 2. The test puts the poison item between two healthy items. Exactly the
    /// poison item must dead-letter, with the handler's own exception and the exhausted attempt
    /// count (1 initial + 1 retry). The healthy items around the failure must process normally.
    /// </remarks>
    [Test]
    [Arguments(DispatchStrategy.Fifo)]
    [Arguments(DispatchStrategy.PriorityRelaxed)]
    [Arguments(DispatchStrategy.PriorityExact)]
    public async Task DlqHandlerFailure_RoutesPerStrategy(DispatchStrategy strategy)
    {
        var handler = new MatrixHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkHandler<string>>(handler);
        services.AddWorkOrchestrator<string>(opts =>
        {
            opts.Capacity = 16;
            opts.WorkerCount = 1;
            opts.DispatchStrategy = strategy;
            opts.FullMode = QueueFullMode.Reject;
        })
        .WithDeadLetterQueue(dlqOpts => dlqOpts.MaxRetries = 1)
        .Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        var first = await orchestrator.EnqueueAsync("ok-1");
        var poison = await orchestrator.EnqueueAsync("poison-item");
        var second = await orchestrator.EnqueueAsync("ok-2");
        await Assert.That(first).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(poison).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(second).IsEqualTo(EnqueueResult.Accepted);

        using var drainCts = new CancellationTokenSource(WaitTimeout);
        await orchestrator.DrainAsync(drainCts.Token);

        var dlq = provider.GetRequiredService<IDeadLetterQueue<string>>();
        var entries = await ReadAllEntriesAsync(dlq);
        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].Work).IsEqualTo("poison-item");
        await Assert.That(entries[0].AttemptCount).IsEqualTo(2);
        await Assert.That(entries[0].Exception is InvalidOperationException).IsTrue();

        var processed = handler.ProcessedSnapshot();
        await Assert.That(processed.Count).IsEqualTo(2);
        await Assert.That(processed).Contains("ok-1");
        await Assert.That(processed).Contains("ok-2");

        await orchestrator.DisposeAsync();
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
    /// <remarks>
    /// The capacity is 2 and the single worker is parked, so the queue state is deterministic while
    /// the test fills to hard capacity (Interactive watermark 1.0). The test parks the worker,
    /// fills to capacity, and then sheds one item. The caller must still receive the typed
    /// rejection (no swallowing). The shed item must be in the dead-letter pathway with the marker
    /// that identifies a rejection. At the end, the test releases the parked worker so that
    /// disposal does not wait.
    /// </remarks>
    [Test]
    [Arguments(DispatchStrategy.PriorityRelaxed)]
    [Arguments(DispatchStrategy.PriorityExact)]
    public async Task DlqAdmissionRejection_RoutesOnPriorityStrategies(DispatchStrategy strategy)
    {
        var handler = new MatrixHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkHandler<string>>(handler);
        services.AddWorkOrchestrator<string>(opts =>
        {
            opts.Capacity = 2;
            opts.WorkerCount = 1;
            opts.DispatchStrategy = strategy;
            opts.FullMode = QueueFullMode.Reject;
        })
        .WithDeadLetterQueue()
        .Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        var dlq = provider.GetRequiredService<IDeadLetterQueue<string>>();

        var parked = await orchestrator.EnqueueAsync("gate", WorkClass.Interactive);
        await Assert.That(parked).IsEqualTo(EnqueueResult.Accepted);
        await handler.GateEntered.WaitAsync(WaitTimeout);

        var first = await orchestrator.EnqueueAsync("keep-1", WorkClass.Interactive);
        var second = await orchestrator.EnqueueAsync("keep-2", WorkClass.Interactive);
        var shed = await orchestrator.EnqueueAsync("shed-item", WorkClass.Interactive);

        await Assert.That(first).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(second).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(shed.IsAccepted).IsFalse();
        await Assert.That(shed.Reason).IsEqualTo(RejectionReason.CapacityExceeded);

        var entries = await ReadAllEntriesAsync(dlq);
        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].Work).IsEqualTo("shed-item");
        await Assert.That(entries[0].AttemptCount).IsEqualTo(0);
        var marker = entries[0].Exception as WorkRejectedException;
        await Assert.That(marker).IsNotNull();
        await Assert.That(marker!.Reason).IsEqualTo(RejectionReason.CapacityExceeded);

        handler.ReleaseGate();

        await orchestrator.DisposeAsync();
    }

    /// <summary>
    /// Acceptance (issue #17, FIFO negative case): the FIFO default keeps
    /// producer-wait semantics at capacity — the overflow enqueue WAITS for space
    /// instead of rejecting, eventually completes Accepted, and the admission
    /// pathway never produces a dead-letter entry.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// The capacity is 2, the single worker is parked, and the DLQ is configured. If FIFO ever made
    /// admission entries, this stack would capture them. The test parks the worker and fills to
    /// capacity. The overflow enqueue must WAIT (producer-wait), not fail fast: the queue is full
    /// and the only consumer is parked, so the enqueue cannot be complete. No admission entry can
    /// exist while the producer waits. When the worker is released, space becomes free and the wait
    /// resolves to acceptance. At the end, everything must be processed, and the FIFO admission
    /// pathway must not have dead-lettered anything.
    /// </remarks>
    [Test]
    public async Task DlqAdmissionAtCapacity_Fifo_WaitsWithoutDeadLettering()
    {
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

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        var dlq = provider.GetRequiredService<IDeadLetterQueue<string>>();

        var parked = await orchestrator.EnqueueAsync("gate");
        await Assert.That(parked).IsEqualTo(EnqueueResult.Accepted);
        await handler.GateEntered.WaitAsync(WaitTimeout);

        var first = await orchestrator.EnqueueAsync("fill-1");
        var second = await orchestrator.EnqueueAsync("fill-2");
        await Assert.That(first).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(second).IsEqualTo(EnqueueResult.Accepted);

        var overflow = orchestrator.EnqueueAsync("overflow");
        await Assert.That(overflow.IsCompleted).IsFalse();

        await Assert.That(dlq.Count).IsEqualTo(0);

        handler.ReleaseGate();
        var overflowResult = await overflow;
        await Assert.That(overflowResult).IsEqualTo(EnqueueResult.Accepted);

        using var drainCts = new CancellationTokenSource(WaitTimeout);
        await orchestrator.DrainAsync(drainCts.Token);

        await Assert.That(handler.ProcessedSnapshot().Count).IsEqualTo(4);
        await Assert.That(dlq.Count).IsEqualTo(0);

        await orchestrator.DisposeAsync();
    }

    /// <summary>
    /// Acceptance (issue #17, all strategies): work-processed events flow through
    /// the event-stream decorator under each strategy, and
    /// <see cref="WorkEnqueuedEvent{TWork}"/> publishes ONLY for accepted enqueues —
    /// a rejected/declined admission never produces an enqueued event.
    /// </summary>
    /// <param name="strategy">The dispatch strategy binding under test.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// <para>The stack has only the event stream (the outermost decorator). The capacity is 4, so
    /// the declined-admission leg is reachable on every strategy through <c>TryEnqueue</c>.</para>
    /// <para>The test subscribes BEFORE it acts. <c>GetEventStreamAsync</c> registers the
    /// subscriber channel eagerly at call time, so no event published after these calls can be
    /// missed. <c>DrainAsync</c> completes the channels, and that ends the collectors.</para>
    /// <para>The test parks the worker and fills to hard capacity (Interactive watermark 1.0). Then
    /// it attempts one <c>TryEnqueue</c> over capacity, which is declined on every strategy (FIFO:
    /// full channel, priority: fail-fast admission). The drain processes the backlog and then
    /// completes the subscriber channels. There must be enqueued events for exactly the five
    /// accepted items and never for the declined overflow. Work-processed events must flow for all
    /// five accepted items.</para>
    /// </remarks>
    [Test]
    [Arguments(DispatchStrategy.Fifo)]
    [Arguments(DispatchStrategy.PriorityRelaxed)]
    [Arguments(DispatchStrategy.PriorityExact)]
    public async Task EventStream_PublishesPerStrategy(DispatchStrategy strategy)
    {
        var handler = new MatrixHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkHandler<string>>(handler);
        services.AddWorkOrchestrator<string>(opts =>
        {
            opts.Capacity = 4;
            opts.WorkerCount = 1;
            opts.DispatchStrategy = strategy;
            opts.FullMode = QueueFullMode.Reject;
        })
        .WithEventStream()
        .Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        var eventStream = orchestrator as IEventStreamOrchestrator<string>;
        await Assert.That(eventStream).IsNotNull();

        var enqueuedWorks = new ConcurrentQueue<string>();
        var completedWorks = new ConcurrentQueue<(string Work, bool Success)>();
        var enqueuedStream = eventStream!.GetEventStreamAsync<WorkEnqueuedEvent<string>>();
        var completedStream = eventStream.GetEventStreamAsync<WorkCompletedEvent<string>>();
        var enqueuedCollector = Task.Run(async () =>
        {
            await foreach (var evt in enqueuedStream)
            {
                enqueuedWorks.Enqueue(evt.Work);
            }
        });
        var completedCollector = Task.Run(async () =>
        {
            await foreach (var evt in completedStream)
            {
                completedWorks.Enqueue((evt.Work, evt.Success));
            }
        });

        var parked = await orchestrator.EnqueueAsync("gate", WorkClass.Interactive);
        await Assert.That(parked).IsEqualTo(EnqueueResult.Accepted);
        await handler.GateEntered.WaitAsync(WaitTimeout);

        for (var i = 0; i < 4; i++)
        {
            var accepted = await orchestrator.EnqueueAsync($"fill-{i}", WorkClass.Interactive);
            await Assert.That(accepted).IsEqualTo(EnqueueResult.Accepted);
        }

        var overflowAdmitted = orchestrator.TryEnqueue("overflow", WorkClass.Interactive);
        await Assert.That(overflowAdmitted).IsFalse();

        handler.ReleaseGate();
        using var drainCts = new CancellationTokenSource(WaitTimeout);
        await orchestrator.DrainAsync(drainCts.Token);
        await Task.WhenAll(enqueuedCollector, completedCollector).WaitAsync(WaitTimeout);

        var enqueued = enqueuedWorks.ToList();
        await Assert.That(enqueued.Count).IsEqualTo(5);
        await Assert.That(enqueued.Contains("overflow")).IsFalse();
        await Assert.That(enqueued.Contains("gate")).IsTrue();

        var completed = completedWorks.ToList();
        await Assert.That(completed.Count).IsEqualTo(5);
        await Assert.That(completed.All(c => c.Success)).IsTrue();
        await Assert.That(completed.Select(c => c.Work).Contains("gate")).IsTrue();
        for (var i = 0; i < 4; i++)
        {
            await Assert.That(completed.Select(c => c.Work).Contains($"fill-{i}")).IsTrue();
        }

        await orchestrator.DisposeAsync();
    }

    /// <summary>
    /// Acceptance (issue #17, all strategies): the existing health check
    /// registration composes with the full decorator stack and reports Healthy on
    /// an idle orchestrator under each strategy — the check reads depth and worker
    /// state through the decorated surface.
    /// </summary>
    /// <param name="strategy">The dispatch strategy binding under test.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// The test uses the full stack plus health checks. It resolves the orchestrator singleton so
    /// that the check observes the live instance. The check with the default name must be
    /// registered and must report Healthy on the idle orchestrator (0% utilization), for every
    /// binding.
    /// </remarks>
    [Test]
    [Arguments(DispatchStrategy.Fifo)]
    [Arguments(DispatchStrategy.PriorityRelaxed)]
    [Arguments(DispatchStrategy.PriorityExact)]
    public async Task HealthCheck_RegistersAndReportsPerStrategy(DispatchStrategy strategy)
    {
        var handler = new MatrixHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkHandler<string>>(handler);
        services.AddWorkOrchestrator<string>(opts =>
        {
            opts.Capacity = 16;
            opts.WorkerCount = 1;
            opts.DispatchStrategy = strategy;
            opts.FullMode = QueueFullMode.Reject;
        })
        .WithDeadLetterQueue()
        .WithOpenTelemetry()
        .WithAutoscaling()
        .WithHealthChecks()
        .Build();

        var provider = services.BuildServiceProvider();

        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();
        await Assert.That(orchestrator).IsNotNull();

        var healthService = provider.GetRequiredService<HealthCheckService>();
        var report = await healthService.CheckHealthAsync();

        await Assert.That(report.Entries.ContainsKey("WorkOrchestrator<String>")).IsTrue();
        await Assert.That(report.Entries["WorkOrchestrator<String>"].Status).IsEqualTo(HealthStatus.Healthy);
        await Assert.That(report.Status).IsEqualTo(HealthStatus.Healthy);

        await orchestrator.DisposeAsync();
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
    /// <remarks>
    /// The full stack has autoscaling and a single built-in worker. The gate item parks that
    /// worker, so only the dynamic worker can drain the backlog. The test makes one dynamic worker
    /// through the decorated surface, and the autoscaling decorator wraps that worker with metrics
    /// instrumentation. The dynamic worker alone must consume the full backlog while the built-in
    /// worker stays parked (the gate item never completes). At the end, the test releases the gate,
    /// drains, and stops the dynamic worker. The dynamic worker exits normally when the drained
    /// queue completes, and cancellation is a backstop. An <c>OperationCanceledException</c> is
    /// expected during this shutdown.
    /// </remarks>
    [Test]
    [Arguments(DispatchStrategy.Fifo)]
    [Arguments(DispatchStrategy.PriorityRelaxed)]
    [Arguments(DispatchStrategy.PriorityExact)]
    public async Task Autoscaling_DynamicWorkers_ConsumePerStrategy(DispatchStrategy strategy)
    {
        var handler = new MatrixHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkHandler<string>>(handler);
        services.AddWorkOrchestrator<string>(opts =>
        {
            opts.Capacity = 64;
            opts.WorkerCount = 1;
            opts.DispatchStrategy = strategy;
            opts.FullMode = QueueFullMode.Reject;
        })
        .WithDeadLetterQueue()
        .WithOpenTelemetry()
        .WithAutoscaling()
        .Build();

        var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<string>>();

        var parked = await orchestrator.EnqueueAsync("gate");
        await Assert.That(parked).IsEqualTo(EnqueueResult.Accepted);
        await handler.GateEntered.WaitAsync(WaitTimeout);

        const int itemCount = 6;
        for (var i = 0; i < itemCount; i++)
        {
            var result = await orchestrator.EnqueueAsync($"item-{i}", (WorkClass)(i % 3));
            await Assert.That(result).IsEqualTo(EnqueueResult.Accepted);
        }

        using var workerCts = new CancellationTokenSource();
        var workerFunc = orchestrator.CreateWorkerFunction();
        var workerTask = Task.Run(() => workerFunc("dynamic-worker", workerCts.Token));

        await handler.WaitForProcessedCountAsync(itemCount, WaitTimeout);
        var processed = handler.ProcessedSnapshot();
        await Assert.That(processed.Count).IsEqualTo(itemCount);
        await Assert.That(processed.Contains("gate")).IsFalse();
        for (var i = 0; i < itemCount; i++)
        {
            await Assert.That(processed).Contains($"item-{i}");
        }

        handler.ReleaseGate();
        using var drainCts = new CancellationTokenSource(WaitTimeout);
        await orchestrator.DrainAsync(drainCts.Token);
        await workerCts.CancelAsync();
        try
        {
            await workerTask.WaitAsync(WaitTimeout);
        }
        catch (OperationCanceledException)
        {
        }

        await orchestrator.DisposeAsync();
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
        await foreach (var entry in dlq.ReadAllAsync())
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
                await Task.Delay(10);
            }
        }

        /// <inheritdoc/>
        public async ValueTask HandleAsync(string work, CancellationToken ct)
        {
            if (work == "gate")
            {
                _gateEntered.TrySetResult();
                await _release.Task.WaitAsync(ct);
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
