// =============================================================================
// <copyright file="WorkOrchestratorDisposalTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Queues;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

namespace Bifrost.Tests.Orchestrator;

/// <summary>
/// Disposal tests for <see cref="WorkOrchestrator{TWork}"/> (DR-3, issue #21): the
/// orchestrator OWNS the queue binding it constructs in its strategy factory, so its
/// <see cref="WorkOrchestrator{TWork}.DisposeAsync"/> must dispose that binding when it
/// is disposable (the priority bindings own a <see cref="SemaphoreSlim"/>). Before DR-3
/// the worker-drain path never disposed <c>_queue</c>, leaking the binding's semaphore.
/// </summary>
/// <remarks>
/// <para>
/// Disposal is asserted via the binding's <c>IsDisposedForTest</c> inspection seam
/// rather than a post-dispose <c>WaitToDequeueAsync</c>: the orchestrator completes the
/// queue during dispose (<c>CompleteQueue</c>), so a subsequent wait takes the
/// post-completion poll branch and never reaches the disposed semaphore — the
/// binding-level disposal tests cover the
/// <c>WaitToDequeueAsync</c> → <see cref="ObjectDisposedException"/> path on a
/// non-completed queue.
/// </para>
/// <para>
/// The FIFO default binding (<see cref="FifoChannelWorkQueue{T}"/>) is non-disposable
/// and must remain unaffected — disposing a FIFO orchestrator completes cleanly with no
/// attempt to dispose a non-disposable queue.
/// </para>
/// </remarks>
public sealed class WorkOrchestratorDisposalTests
{
    /// <summary>
    /// Verifies that <see cref="WorkOrchestrator{TWork}.DisposeAsync"/> disposes the
    /// owned MultiQueue priority binding (DR-3): after disposal, the binding reports
    /// disposed.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WorkOrchestrator_DisposeAsync_DisposesPriorityBinding()
    {
        // Arrange — a priority MultiQueue orchestrator (its binding owns a semaphore).
        var orchestrator = CreateOrchestrator(new WorkOrchestratorOptions
        {
            WorkerCount = 1,
            DispatchStrategy = DispatchStrategy.PriorityMultiQueue,
        });

        var binding = (ConcurrentPriorityWorkQueue<string>)orchestrator.WorkQueue;
        await Assert.That(binding.IsDisposedForTest).IsFalse();

        // Act — disposing the orchestrator must dispose the owned binding.
        await orchestrator.DisposeAsync().ConfigureAwait(false);

        // Assert — the binding's owned semaphore was released.
        await Assert.That(binding.IsDisposedForTest).IsTrue();
    }

    /// <summary>
    /// Verifies the same disposal for the lock-based priority binding (DR-3): it also
    /// owns a <see cref="SemaphoreSlim"/>, so
    /// <see cref="WorkOrchestrator{TWork}.DisposeAsync"/> must release it.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WorkOrchestrator_DisposeAsync_DisposesLockingPriorityBinding()
    {
        // Arrange
        var orchestrator = CreateOrchestrator(new WorkOrchestratorOptions
        {
            WorkerCount = 1,
            DispatchStrategy = DispatchStrategy.PriorityLocking,
        });

        var binding = (LockingPriorityWorkQueue<string>)orchestrator.WorkQueue;
        await Assert.That(binding.IsDisposedForTest).IsFalse();

        // Act
        await orchestrator.DisposeAsync().ConfigureAwait(false);

        // Assert
        await Assert.That(binding.IsDisposedForTest).IsTrue();
    }

    /// <summary>
    /// Verifies that disposing a FIFO-strategy orchestrator completes cleanly (DR-3):
    /// the default <see cref="FifoChannelWorkQueue{T}"/> is non-disposable, so the
    /// queue-disposal branch must be a no-op for it — no throw.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WorkOrchestrator_DisposeAsync_Fifo_DisposesCleanly()
    {
        // Arrange — default FIFO strategy: the binding is non-disposable.
        var orchestrator = CreateOrchestrator(new WorkOrchestratorOptions
        {
            WorkerCount = 1,
        });

        // Act + Assert — disposal completes without throwing.
        await Assert.That(async () => await orchestrator.DisposeAsync().ConfigureAwait(false))
            .ThrowsNothing();
    }

    /// <summary>
    /// Verifies that a second <see cref="WorkOrchestrator{TWork}.DisposeAsync"/> on a
    /// priority orchestrator stays safe (DR-3): the binding's own
    /// <c>Interlocked.Exchange</c> idempotency guard absorbs the double-dispose, so the
    /// orchestrator's repeated disposal does not throw.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task WorkOrchestrator_DisposeAsyncTwice_Priority_DoesNotThrow()
    {
        // Arrange
        var orchestrator = CreateOrchestrator(new WorkOrchestratorOptions
        {
            WorkerCount = 1,
            DispatchStrategy = DispatchStrategy.PriorityMultiQueue,
        });

        // Act + Assert — both disposals complete without throwing.
        await orchestrator.DisposeAsync().ConfigureAwait(false);
        await Assert.That(async () => await orchestrator.DisposeAsync().ConfigureAwait(false))
            .ThrowsNothing();
    }

    /// <summary>
    /// Creates a <see cref="WorkOrchestrator{TWork}"/> over a no-op substitute handler
    /// with the supplied options (validation bypassed via
    /// <see cref="Options.Create{TOptions}(TOptions)"/>, so any worker count is usable).
    /// </summary>
    /// <param name="options">The orchestrator options to construct with.</param>
    /// <returns>The constructed orchestrator.</returns>
    private static WorkOrchestrator<string> CreateOrchestrator(WorkOrchestratorOptions options)
        => new(
            Substitute.For<IWorkHandler<string>>(),
            Options.Create(options),
            NullLogger<WorkOrchestrator<string>>.Instance);
}
