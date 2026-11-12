// =============================================================================
// <copyright file="PriorityWakePermitTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost;
using Bifrost.Core;
using Bifrost.Queues;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bifrost.Tests.Queues;

/// <summary>
/// A priority drain must consume the wake permit that funded each extra item.
/// </summary>
public sealed class PriorityWakePermitTests
{
    /// <summary>
    /// Draining a queued burst leaves no surplus permits behind the last item.
    /// </summary>
    [Test]
    public async Task RelaxedPriorityDrain_LastItem_HasNoSurplusWake()
    {
        await AssertNoSurplusAfterDrain(DispatchStrategy.PriorityRelaxed).ConfigureAwait(false);
    }

    /// <summary>
    /// The exact priority binding matches the relaxed binding's permit accounting.
    /// </summary>
    [Test]
    public async Task ExactPriorityDrain_LastItem_HasNoSurplusWake()
    {
        await AssertNoSurplusAfterDrain(DispatchStrategy.PriorityExact).ConfigureAwait(false);
    }

    private static async Task AssertNoSurplusAfterDrain(DispatchStrategy strategy)
    {
        const int total = 30;
        var holder = new Holder();
        var handler = new BlockingHandler(holder, total);
        await using var orchestrator = new WorkOrchestrator<int>(
            handler,
            Options.Create(new WorkOrchestratorOptions
            {
                Capacity = 100,
                WorkerCount = 1,
                FullMode = QueueFullMode.Reject,
                DispatchStrategy = strategy,
            }),
            NullLogger<WorkOrchestrator<int>>.Instance);

        holder.Queue = orchestrator.WorkQueue;
        await orchestrator.EnqueueAsync(0).ConfigureAwait(false);
        await handler.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        for (var i = 1; i < total; i++)
        {
            var result = await orchestrator.EnqueueAsync(i).ConfigureAwait(false);
            await Assert.That(result.IsAccepted).IsTrue();
        }

        handler.Release.TrySetResult();
        await handler.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.That(handler.SurplusAtLastItem).IsEqualTo(0);
    }

    private sealed class Holder
    {
        public IWorkQueue<WorkEnvelope<int>>? Queue { get; set; }
    }

    private sealed class BlockingHandler(Holder holder, int total) : IWorkHandler<int>
    {
        private int seen;

        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SurplusAtLastItem { get; private set; } = -1;

        public async ValueTask HandleAsync(int work, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref this.seen);
            if (n == 1)
            {
                this.FirstEntered.TrySetResult();
                await this.Release.Task.WaitAsync(ct).ConfigureAwait(false);
            }

            if (n == total)
            {
                this.SurplusAtLastItem = holder.Queue switch
                {
                    ConcurrentPriorityWorkQueue<int> relaxed => relaxed.PendingWakeCountForTest,
                    LockingPriorityWorkQueue<int> exact => exact.PendingWakeCountForTest,
                    _ => -1,
                };
                this.Finished.TrySetResult();
            }
        }
    }
}
