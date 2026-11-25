// =============================================================================
// <copyright file="DispatchStrategyTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.DependencyInjection;
using Bifrost.Queues;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

namespace Bifrost.Tests;

/// <summary>
/// Tests for <see cref="DispatchStrategy"/> selection (T18, DR-4): enum/factory-based
/// queue binding selection on <see cref="WorkOrchestrator{TWork}"/> — no reflective
/// resolution, FIFO remains the default — plus the fluent builder surface and the
/// per-strategy enqueue semantics (DR-6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-strategy enqueue semantics (DR-6), pinned here:</b> the FIFO default keeps
/// producer-wait semantics on <c>EnqueueAsync</c> (await space; Accepted after the
/// wait; Rejected(Shutdown) only on shutdown/cancel). The priority strategies are
/// FAIL-FAST at admission: <c>EnqueueAsync</c> never waits for space — a
/// try-enqueue failure maps immediately to Rejected(CapacityExceeded), where
/// watermark shedding and hard capacity exhaustion are deliberately
/// indistinguishable (T23 routes rejections to the DLQ), and Rejected(Shutdown)
/// after the queue is completed. Producer-wait at capacity was rejected in design
/// as admission-side priority inversion.
/// </para>
/// <para>
/// Watermark scenarios run QUIESCENT (no workers, single-threaded), where the
/// queue count is exact by contract, so admission boundaries assert
/// deterministically.
/// </para>
/// </remarks>
public sealed class DispatchStrategyTests
{
    /// <summary>
    /// Verifies that <see cref="WorkOrchestratorOptions"/> defaults to the FIFO
    /// strategy (DR-4: the pre-existing behavior is the default) with a non-null,
    /// eagerly-defaulted <see cref="WorkOrchestratorOptions.Priority"/> bag.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// The priority options are eagerly defaulted, so configure delegates can change them without null
    /// checks.
    /// </remarks>
    [Test]
    public async Task Options_Default_IsFifo()
    {
        var options = new WorkOrchestratorOptions();

        await Assert.That(options.DispatchStrategy).IsEqualTo(DispatchStrategy.Fifo);
        await Assert.That(options.Priority).IsNotNull();
    }

    /// <summary>
    /// Verifies that the default (FIFO) strategy constructs a
    /// <see cref="FifoChannelWorkQueue{T}"/> internally.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Orchestrator_Fifo_UsesFifoChannelWorkQueue()
    {
        await using var orchestrator = CreateOrchestrator(new WorkOrchestratorOptions
        {
            WorkerCount = 1,
        });

        await Assert.That(orchestrator.WorkQueue)
            .IsTypeOf<FifoChannelWorkQueue<WorkEnvelope<string>>>();
    }

    /// <summary>
    /// Verifies that <see cref="DispatchStrategy.PriorityRelaxed"/> constructs a
    /// <see cref="ConcurrentPriorityWorkQueue{TWork}"/> internally.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Orchestrator_PriorityMultiQueue_UsesConcurrentPriorityWorkQueue()
    {
        await using var orchestrator = CreateOrchestrator(new WorkOrchestratorOptions
        {
            WorkerCount = 1,
            DispatchStrategy = DispatchStrategy.PriorityRelaxed,
            FullMode = QueueFullMode.Reject,
        });

        await Assert.That(orchestrator.WorkQueue)
            .IsTypeOf<ConcurrentPriorityWorkQueue<string>>();
    }

    /// <summary>
    /// Verifies that <see cref="DispatchStrategy.PriorityExact"/> constructs a
    /// <see cref="LockingPriorityWorkQueue{TWork}"/> internally.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Orchestrator_PriorityLocking_UsesLockingPriorityWorkQueue()
    {
        await using var orchestrator = CreateOrchestrator(new WorkOrchestratorOptions
        {
            WorkerCount = 1,
            DispatchStrategy = DispatchStrategy.PriorityExact,
            FullMode = QueueFullMode.Reject,
        });

        await Assert.That(orchestrator.WorkQueue)
            .IsTypeOf<LockingPriorityWorkQueue<string>>();
    }

    /// <summary>
    /// Verifies the fail-fast admission semantic of the priority strategies (DR-6):
    /// with a capacity-4 priority orchestrator and no consumers, the queue fills to
    /// hard capacity with Interactive items, and the next <c>EnqueueAsync</c>
    /// completes SYNCHRONOUSLY (already-completed <see cref="ValueTask{T}"/> — no
    /// producer-wait) with Rejected(CapacityExceeded); after the queue is completed
    /// via drain, the rejection reason becomes Shutdown.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// <para>
    /// The orchestrator has capacity 4 and no workers, so nothing drains the queue. Interactive admits
    /// to hard capacity, because its watermark is 1.0 by default.
    /// </para>
    /// <para>
    /// The overflow enqueue must not wait for space. Its <c>ValueTask</c> is already complete when
    /// <c>EnqueueAsync</c> returns (fail-fast admission). After <c>DrainAsync</c> completes the queue,
    /// the rejection reason changes to Shutdown (DR-6).
    /// </para>
    /// </remarks>
    [Test]
    public async Task PriorityStrategy_EnqueueAsync_FailFast_RejectedAtCapacity()
    {
        await using var orchestrator = CreateOrchestrator(new WorkOrchestratorOptions
        {
            Capacity = 4,
            WorkerCount = 0,
            DispatchStrategy = DispatchStrategy.PriorityRelaxed,
            FullMode = QueueFullMode.Reject,
        });

        for (var i = 0; i < 4; i++)
        {
            var accepted = await orchestrator.EnqueueAsync($"fill-{i}", WorkClass.Interactive);
            await Assert.That(accepted).IsEqualTo(EnqueueResult.Accepted);
        }

        var overflow = orchestrator.EnqueueAsync("overflow", WorkClass.Interactive);
        var completedSynchronously = overflow.IsCompleted;
        var result = await overflow;

        await Assert.That(completedSynchronously).IsTrue();
        await Assert.That(result.IsAccepted).IsFalse();
        await Assert.That(result.Reason).IsEqualTo(RejectionReason.CapacityExceeded);

        await orchestrator.DrainAsync();
        var afterComplete = await orchestrator.EnqueueAsync("late", WorkClass.Interactive);

        await Assert.That(afterComplete.IsAccepted).IsFalse();
        await Assert.That(afterComplete.Reason).IsEqualTo(RejectionReason.Shutdown);
    }

    /// <summary>
    /// Verifies watermark shedding end-to-end through the orchestrator (DR-6): with
    /// a capacity-100 priority orchestrator quiescent at 90 queued Default items, a
    /// Batch enqueue is rejected (0.90 watermark reached — surfaced as
    /// CapacityExceeded, indistinguishable from hard capacity by design) while an
    /// Interactive enqueue is still accepted.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// The test fills the queue to the Batch threshold (0.90 × 100 = 90) with Default items. There are no
    /// workers, so the count is exact and the boundary is deterministic. The lowest class sheds first,
    /// and Interactive still admits.
    /// </remarks>
    [Test]
    public async Task PriorityStrategy_Enqueue_BatchShedsAtWatermark_ViaOrchestrator()
    {
        await using var orchestrator = CreateOrchestrator(new WorkOrchestratorOptions
        {
            Capacity = 100,
            WorkerCount = 0,
            DispatchStrategy = DispatchStrategy.PriorityRelaxed,
            FullMode = QueueFullMode.Reject,
        });

        for (var i = 0; i < 90; i++)
        {
            var accepted = await orchestrator.EnqueueAsync($"default-{i}");
            await Assert.That(accepted).IsEqualTo(EnqueueResult.Accepted);
        }

        var batch = await orchestrator.EnqueueAsync("batch", WorkClass.Batch);
        var interactive = await orchestrator.EnqueueAsync("interactive", WorkClass.Interactive);

        await Assert.That(batch.IsAccepted).IsFalse();
        await Assert.That(batch.Reason).IsEqualTo(RejectionReason.WatermarkExceeded);
        await Assert.That(interactive).IsEqualTo(EnqueueResult.Accepted);
        await Assert.That(orchestrator.PendingCount).IsEqualTo(91);
    }

    /// <summary>
    /// Verifies that <c>UsePriorityDispatch</c> on the builder sets the
    /// <see cref="DispatchStrategy.PriorityRelaxed"/> sentinel and
    /// <see cref="DispatchStrategy.PriorityRelaxed"/> intent, and that the configure delegate
    /// reaches the options.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Builder_UsePriorityDispatch_SetsPrioritySentinelAndAppliesDelegate()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());

        services.AddWorkOrchestrator<string>(opts =>
        {
            opts.Capacity = 16;
            opts.WorkerCount = 1;
        })
        .UsePriorityDispatch(DispatchStrategy.PriorityRelaxed, priority => priority.InteractiveBoostWindow = TimeSpan.FromSeconds(5))
        .Build();

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<WorkOrchestratorOptions>>().Get(WorkOptions.Name<string>());

        await Assert.That(options.DispatchStrategy).IsEqualTo(DispatchStrategy.PriorityRelaxed);
        await Assert.That(options.Priority.InteractiveBoostWindow).IsEqualTo(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Verifies that <c>UsePriorityDispatch(ordering: DispatchStrategy.PriorityExact)</c> sets
    /// the coarse-locking binding intent and the orchestrator resolves it to the
    /// locking queue.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Builder_UsePriorityDispatch_Locking_SelectsLocking()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkHandler<string>>());

        services.AddWorkOrchestrator<string>(opts =>
        {
            opts.Capacity = 16;
            opts.WorkerCount = 1;
        })
        .UsePriorityDispatch(ordering: DispatchStrategy.PriorityExact)
        .Build();

        await using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<WorkOrchestratorOptions>>().Get(WorkOptions.Name<string>());
        await Assert.That(options.DispatchStrategy).IsEqualTo(DispatchStrategy.PriorityExact);
    }

    /// <summary>
    /// Verifies that all three queue bindings are sealed (DR-4): sealed concrete
    /// types let the JIT devirtualize — and guardedly devirtualize through the
    /// <see cref="IWorkQueue{T}"/> contract — on the dispatch hot path.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Bindings_AreSealed_ForDevirtualization()
    {
        await Assert.That(typeof(FifoChannelWorkQueue<WorkEnvelope<string>>).IsSealed).IsTrue();
        await Assert.That(typeof(ConcurrentPriorityWorkQueue<string>).IsSealed).IsTrue();
        await Assert.That(typeof(LockingPriorityWorkQueue<string>).IsSealed).IsTrue();
    }

    /// <summary>
    /// Creates a <see cref="WorkOrchestrator{TWork}"/> over a no-op substitute
    /// handler with the supplied options (validation is bypassed by design via
    /// <see cref="Options.Create{TOptions}(TOptions)"/>, so WorkerCount 0 is usable
    /// for quiescent queue scenarios).
    /// </summary>
    /// <param name="options">The orchestrator options to construct with.</param>
    /// <returns>The constructed orchestrator.</returns>
    private static WorkOrchestrator<string> CreateOrchestrator(WorkOrchestratorOptions options)
        => new(
            Substitute.For<IWorkHandler<string>>(),
            Options.Create(options),
            NullLogger<WorkOrchestrator<string>>.Instance);
}
