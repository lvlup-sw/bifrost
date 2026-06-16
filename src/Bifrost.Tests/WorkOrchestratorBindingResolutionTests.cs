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
    /// <see cref="PriorityBindingResolver"/> for the current machine's
    /// <see cref="Environment.ProcessorCount"/> and the configured capacity.
    /// Machine-independent: asserts consistency with the resolver, not a hardcoded value.
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

        // What the resolver says Auto should pick on this machine.
        var resolvedStrategy = PriorityBindingResolver.Resolve(
            PriorityBinding.Auto,
            Environment.ProcessorCount,
            capacity);

        var expectedBinding = resolvedStrategy == DispatchStrategy.PriorityLocking
            ? PriorityBinding.Locking
            : PriorityBinding.MultiQueue;

        await Assert.That(orchestrator.ResolvedBinding).IsEqualTo(expectedBinding);
    }
}
