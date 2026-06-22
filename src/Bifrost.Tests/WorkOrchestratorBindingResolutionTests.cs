// =============================================================================
// <copyright file="WorkOrchestratorBindingResolutionTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Queues;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using TUnit.Core;

namespace Bifrost.Tests;

/// <summary>
/// Tests for <see cref="WorkOrchestrator{TWork}"/> priority-binding resolution (T8):
/// the <see cref="DispatchStrategy.Priority"/> sentinel is resolved at construction
/// from <see cref="PriorityDispatchOptions.Binding"/>, and the result is exposed
/// as <see cref="WorkOrchestrator{TWork}.ResolvedBinding"/>.
/// </summary>
[Property("Category", "Unit")]
public class WorkOrchestratorBindingResolutionTests
{
    private static WorkOrchestrator<string> CreateOrchestrator(WorkOrchestratorOptions options)
        => new(
            Substitute.For<IWorkHandler<string>>(),
            Options.Create(options),
            NullLogger<WorkOrchestrator<string>>.Instance);

    // ─── Explicit binding pass-through ───────────────────────────────────────

    /// <summary>
    /// Verifies that an explicit <see cref="PriorityBinding.MultiQueue"/> binding
    /// bypasses the heuristic and resolves to <see cref="PriorityBinding.MultiQueue"/>.
    /// </summary>
    [Test]
    public async Task Constructor_ExplicitMultiQueue_ResolvesMultiQueue()
    {
        await using var orchestrator = CreateOrchestrator(new WorkOrchestratorOptions
        {
            WorkerCount = 0,
            DispatchStrategy = DispatchStrategy.Priority,
            Priority = { Binding = PriorityBinding.MultiQueue },
        });

        await Assert.That(orchestrator.ResolvedBinding).IsEqualTo(PriorityBinding.MultiQueue);
        await Assert.That(orchestrator.WorkQueue).IsTypeOf<ConcurrentPriorityWorkQueue<string>>();
    }

    /// <summary>
    /// Verifies that an explicit <see cref="PriorityBinding.Locking"/> binding
    /// bypasses the heuristic and resolves to <see cref="PriorityBinding.Locking"/>.
    /// </summary>
    [Test]
    public async Task Constructor_ExplicitLocking_ResolvesLocking()
    {
        await using var orchestrator = CreateOrchestrator(new WorkOrchestratorOptions
        {
            WorkerCount = 0,
            DispatchStrategy = DispatchStrategy.Priority,
            Priority = { Binding = PriorityBinding.Locking },
        });

        await Assert.That(orchestrator.ResolvedBinding).IsEqualTo(PriorityBinding.Locking);
        await Assert.That(orchestrator.WorkQueue).IsTypeOf<LockingPriorityWorkQueue<string>>();
    }

    /// <summary>
    /// Verifies that FIFO strategy results in a null <see cref="WorkOrchestrator{TWork}.ResolvedBinding"/>
    /// (no priority binding to resolve).
    /// </summary>
    [Test]
    public async Task Constructor_Fifo_ResolvedBindingIsNull()
    {
        await using var orchestrator = CreateOrchestrator(new WorkOrchestratorOptions
        {
            WorkerCount = 0,
            DispatchStrategy = DispatchStrategy.Fifo,
        });

        await Assert.That(orchestrator.ResolvedBinding).IsNull();
    }

    /// <summary>
    /// Verifies that <see cref="PriorityBinding.Auto"/> resolution is consistent with
    /// <see cref="PriorityBindingResolver"/> — which now always resolves Auto to the
    /// MultiQueue regardless of capacity or core count (#46). Asserts consistency with
    /// the resolver rather than a hardcoded value.
    /// </summary>
    [Test]
    public async Task Constructor_Auto_ResolvedBindingMatchesResolver()
    {
        const int capacity = 128;
        await using var orchestrator = CreateOrchestrator(new WorkOrchestratorOptions
        {
            WorkerCount = 0,
            Capacity = capacity,
            DispatchStrategy = DispatchStrategy.Priority,
            Priority = { Binding = PriorityBinding.Auto },
        });

        // The resolver now resolves Auto to MultiQueue unconditionally.
        var resolvedStrategy = PriorityBindingResolver.Resolve(PriorityBinding.Auto);

        var expectedBinding = resolvedStrategy == DispatchStrategy.PriorityLocking
            ? PriorityBinding.Locking
            : PriorityBinding.MultiQueue;

        await Assert.That(orchestrator.ResolvedBinding).IsEqualTo(expectedBinding);
        await Assert.That(orchestrator.ResolvedBinding).IsEqualTo(PriorityBinding.MultiQueue);
    }

    /// <summary>
    /// Verifies that <see cref="PriorityBinding.Auto"/> never selects Locking, even at a very
    /// high capacity where the old heuristic would have flipped to the locking heap (#46): the
    /// resolved binding stays <see cref="PriorityBinding.MultiQueue"/> and the constructed queue
    /// is the MultiQueue-backed <see cref="ConcurrentPriorityWorkQueue{TWork}"/>, not
    /// <see cref="LockingPriorityWorkQueue{TWork}"/>.
    /// </summary>
    [Test]
    [Arguments(1)]
    [Arguments(128)]
    [Arguments(10_000)]
    public async Task Constructor_Auto_AtAnyCapacity_YieldsMultiQueueNeverLocking(int capacity)
    {
        await using var orchestrator = CreateOrchestrator(new WorkOrchestratorOptions
        {
            WorkerCount = 0,
            Capacity = capacity,
            DispatchStrategy = DispatchStrategy.Priority,
            Priority = { Binding = PriorityBinding.Auto },
        });

        await Assert.That(orchestrator.ResolvedBinding).IsEqualTo(PriorityBinding.MultiQueue);
        await Assert.That(orchestrator.WorkQueue).IsTypeOf<ConcurrentPriorityWorkQueue<string>>();
    }

    /// <summary>
    /// Verifies that the <see cref="LockingPriorityWorkQueue{TWork}"/> is constructed ONLY for an
    /// explicit <see cref="PriorityBinding.Locking"/> request (#46): an explicit Locking binding,
    /// even at a small capacity where the old Auto heuristic would also have chosen locking, is the
    /// sole route to the locking heap.
    /// </summary>
    [Test]
    public async Task Constructor_ExplicitLocking_IsTheOnlyRouteToLockingQueue()
    {
        await using var orchestrator = CreateOrchestrator(new WorkOrchestratorOptions
        {
            WorkerCount = 0,
            Capacity = 32,
            DispatchStrategy = DispatchStrategy.Priority,
            Priority = { Binding = PriorityBinding.Locking },
        });

        await Assert.That(orchestrator.ResolvedBinding).IsEqualTo(PriorityBinding.Locking);
        await Assert.That(orchestrator.WorkQueue).IsTypeOf<LockingPriorityWorkQueue<string>>();
    }

    /// <summary>
    /// Verifies <see cref="WorkOrchestrator{TWork}.ResolvedBinding"/> is populated even when the
    /// concrete <see cref="DispatchStrategy.PriorityLocking"/> strategy is set directly, bypassing
    /// the <see cref="DispatchStrategy.Priority"/> sentinel (and <c>UsePriorityDispatch</c>).
    /// </summary>
    [Test]
    public async Task Constructor_DirectPriorityLockingStrategy_ResolvedBindingIsLocking()
    {
        await using var orchestrator = CreateOrchestrator(new WorkOrchestratorOptions
        {
            WorkerCount = 0,
            DispatchStrategy = DispatchStrategy.PriorityLocking,
        });

        await Assert.That(orchestrator.ResolvedBinding).IsEqualTo(PriorityBinding.Locking);
        await Assert.That(orchestrator.WorkQueue).IsTypeOf<LockingPriorityWorkQueue<string>>();
    }
}
