// =============================================================================
// <copyright file="PriorityDispatchAcceptanceTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;
using System.Numerics;

using Bifrost.Core;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Acceptance;

/// <summary>
/// End-to-end acceptance tests for the issue #17 criterion (T25, DR-5):
/// "With the priority strategy enabled: an interactive-class item enqueued behind
/// N batch items dispatches next (modulo documented relaxation), under
/// multi-producer/multi-consumer load." Exercised through the public orchestrator
/// API (<see cref="WorkOrchestrator{TWork}"/> constructed from
/// <see cref="WorkOrchestratorOptions.DispatchStrategy"/>), against BOTH priority
/// bindings.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-binding shape of "next" (DR-5):</b> the locking binding dequeues the
/// exact minimum virtual-time key, so "dispatches next" is deterministic and
/// asserted exactly. The MultiQueue binding's relaxed two-choice dequeue carries a
/// documented expected rank error of <c>(5/6)·n</c> (n = sub-queue count), so
/// "next" is rank-bounded, not exact — asserted as a median dispatch position over
/// independent trials against a bound derived honestly from that contract (see the
/// in-test derivation; the bound is provably below the FIFO position, never
/// vacuous).
/// </para>
/// <para>
/// The gated-backlog arrangement: a single worker is captured by a gate item held
/// open inside the handler, the batch backlog and then the interactive item are
/// enqueued behind it on a frozen <see cref="FakeTimeProvider"/> (identical
/// enqueue timestamps, so the 30 s interactive boost is the strict global minimum
/// key — determinism where it helps), and the gate is released. The
/// multi-producer/multi-consumer leg uses real concurrency and real time, because
/// the criterion demands load.
/// </para>
/// <para>
/// <b>Why <c>[NotInParallel]</c>:</b> the load leg measures real queue-wait
/// durations across ~600 small work items; running it alongside the rest of the
/// suite starves the thread pool and stretches the drain past its guard timeout
/// (observed: 60 s drain cancellation under full-suite parallelism vs ~200 ms in
/// isolation). The load the criterion demands must come from the test's own
/// producers and workers — not from unrelated tests — so the class runs
/// exclusively.
/// </para>
/// </remarks>
[Property("Category", "Acceptance")]
[NotInParallel]
public sealed class PriorityDispatchAcceptanceTests
{
    /// <summary>
    /// N — the batch backlog the interactive item is enqueued behind (the issue #17
    /// criterion's "N batch items").
    /// </summary>
    private const int BatchBacklog = 64;

    /// <summary>
    /// K — independent trials for the MultiQueue median-position assertion. The
    /// median of 20 i.i.d. trials concentrates hard: it exceeds a per-trial
    /// q-quantile only when ≥ 10 of 20 trials individually exceed it.
    /// </summary>
    private const int TrialCount = 20;

    /// <summary>
    /// Per-step ceiling for operations expected to complete promptly.
    /// </summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Acceptance (issue #17, locking binding): an Interactive item enqueued behind
    /// <see cref="BatchBacklog"/> Batch items is THE next dispatch after the
    /// in-flight one. The locking binding pops the exact minimum key, and on the
    /// frozen clock the interactive key is strictly (30 s boost) below every batch
    /// key, so the ordering is deterministic — asserted exactly, no relaxation.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Interactive_EnqueuedBehindNBatch_DispatchesNext_Locking()
    {
        // Arrange / Act — gate a single worker on the first item, pile the batch
        // backlog plus one interactive item behind it, release, drain.
        var order = await RunGatedBacklogTrialAsync(DispatchStrategy.PriorityLocking).ConfigureAwait(false);

        // Assert — the gate dispatched first (it captured the worker), and the
        // interactive item is the IMMEDIATE next dispatch despite 64 batch items
        // having been enqueued ahead of it.
        await Assert.That(order.Count).IsEqualTo(BatchBacklog + 2);
        await Assert.That(order[0]).IsEqualTo("gate");
        await Assert.That(order[1]).IsEqualTo("interactive");

        // The remainder is exactly the batch backlog (no loss, no duplication).
        var tail = order.Skip(2).ToList();
        await Assert.That(tail.All(w => w.StartsWith("batch-", StringComparison.Ordinal))).IsTrue();
        await Assert.That(tail.Distinct().Count()).IsEqualTo(BatchBacklog);
    }

    /// <summary>
    /// Acceptance (issue #17, MultiQueue binding): an Interactive item enqueued
    /// behind <see cref="BatchBacklog"/> Batch items dispatches EARLY — its median
    /// dispatch position over <see cref="TrialCount"/> independent trials is within
    /// a bound derived from the DR-5 rank-error contract. "Next" is rank-bounded
    /// here, not exact: the two-choice dequeue is relaxed by design.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// <para>
    /// <b>Bound derivation (from the documented rank-error contract — never
    /// vacuous).</b> The MultiQueue's relaxed <c>TryDequeue</c> has a documented
    /// expected rank error of <c>(5/6)·n</c>, where
    /// <c>n = RoundUpToPowerOf2(4 × ProcessorCount)</c> is the sub-queue count
    /// (see <c>ConcurrentPriorityQueue.TryDequeue</c> remarks). That figure
    /// describes a POPULATED queue (items ≫ n). In this trial only
    /// <c>N + 1 = 65</c> items exist, so the relaxation universe collapses: at most
    /// <c>min(n, N + 1)</c> sub-queues are non-empty, and the rank error available
    /// to defer the boosted item is bounded by the occupied-sub-queue count, never
    /// by n itself.
    /// </para>
    /// <para>
    /// <b>Mechanism backing the collapse.</b> On the frozen clock the interactive
    /// item is the strict global-minimum key (30 s boost), so it sits at the ROOT
    /// of whichever sub-queue uniform placement (stickiness 1) put it in, and it
    /// wins every two-choice comparison whose sample includes its sub-queue; the
    /// verification-scan fallback selects its sub-queue with probability
    /// ≥ 1/occupied by exchangeability of uniform placement. Every single-consumer
    /// pop therefore lands on it with probability ≥ 1/occupied, and `occupied`
    /// shrinks as the backlog drains — its dispatch position concentrates well
    /// below the occupied count.
    /// </para>
    /// <para>
    /// <b>The bound:</b> <c>1 + ⌈(5/6) · min(n, N)⌉</c> — one pop to take the item,
    /// plus the contract's 5/6 factor applied to the collapsed universe. On any
    /// host with n ≥ 64 this evaluates to 55, strictly below the vacuity line of
    /// N + 1 = 65 (a FIFO queue dispatches the interactive item at exactly
    /// position 65 — the kill-probe evidence for this test); smaller hosts tighten
    /// it further (n = 32 → 28). A self-check below asserts non-vacuity on every
    /// host.
    /// </para>
    /// <para>
    /// <b>Why the MEDIAN of K = 20 is safe against this bound:</b> even granting
    /// each trial a pessimistic 15% chance of exceeding the bound (the
    /// uniform-drain model predicts well under that), the sample median exceeds it
    /// only when ≥ 10 of 20 trials do:
    /// <c>P(Binomial(20, 0.15) ≥ 10) ≈ 2×10⁻⁵</c>.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Interactive_EnqueuedBehindNBatch_DispatchesEarly_MultiQueue()
    {
        // Arrange — the rank-error bound, computed from the same formula the
        // structure documents (sub-queue count default).
        var subQueueCount = (int)BitOperations.RoundUpToPowerOf2((uint)(4 * Environment.ProcessorCount));
        var collapsedUniverse = Math.Min(subQueueCount, BatchBacklog);
        var bound = 1 + (int)Math.Ceiling(5.0 / 6.0 * collapsedUniverse);

        // Self-check: the bound must be strictly below the FIFO position (N + 1) on
        // EVERY host, or the assertion would be vacuous.
        await Assert.That(bound).IsLessThan(BatchBacklog + 1);

        // Act — K independent trials, each recording the interactive item's
        // dispatch position among the post-gate dispatches (1 = the very next).
        var positions = new List<int>(TrialCount);
        for (var trial = 0; trial < TrialCount; trial++)
        {
            var order = await RunGatedBacklogTrialAsync(DispatchStrategy.PriorityMultiQueue).ConfigureAwait(false);

            await Assert.That(order.Count).IsEqualTo(BatchBacklog + 2);
            await Assert.That(order[0]).IsEqualTo("gate");

            // Post-gate position: order[0] is the gate, so the index within the
            // full order IS the 1-based post-gate dispatch position.
            var position = order.IndexOf("interactive");
            await Assert.That(position).IsGreaterThanOrEqualTo(1);
            positions.Add(position);
        }

        // Assert — median over the trials within the rank-error-derived bound.
        var sorted = positions.Order().ToList();
        var median = (sorted[(TrialCount / 2) - 1] + sorted[TrialCount / 2]) / 2.0;
        await Assert.That(median).IsLessThanOrEqualTo(bound);
    }

    /// <summary>
    /// Acceptance (issue #17, both bindings): under genuine multi-producer/
    /// multi-consumer load — 4 mixed-class producers, 4 workers with small
    /// simulated work — the mean dispatch wait orders strictly by class:
    /// Interactive &lt; Default &lt; Batch. Measured through the orchestrator's
    /// internal <c>QueueWaitObserved</c> hook (the same seam the OpenTelemetry
    /// queue-wait histogram attaches to), with real time and real concurrency
    /// because the criterion demands load.
    /// </summary>
    /// <param name="strategy">The priority binding under test.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// <para>
    /// <b>Why <see cref="PriorityDispatchOptions.BatchPenaltyWindow"/> is set:</b>
    /// by default Batch is unboosted, not penalized — Default and Batch then share
    /// the same virtual-time key and their mean waits would tie, not order. The
    /// three-way strict ordering this criterion asserts requires the penalty
    /// window, which is exactly the public knob documented for it.
    /// </para>
    /// <para>
    /// <b>Statistical generosity:</b> with ~600 backlogged items draining at
    /// ~4 items per delay quantum, class groups occupy distinct thirds of the
    /// drain, separating the means by ~100+ ms; the assertion demands only strict
    /// ordering plus a 20 ms Batch−Interactive separation floor. The floor is what
    /// makes the FIFO kill-probe deterministic: with FIFO and per-producer
    /// pseudo-random class interleaving, per-class means differ only by ±few-ms
    /// noise around zero, which cannot clear 20 ms.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(DispatchStrategy.PriorityMultiQueue)]
    [Arguments(DispatchStrategy.PriorityLocking)]
    public async Task Interactive_UnderMultiProducerMultiConsumerLoad_LowerMeanWaitThanBatch(DispatchStrategy strategy)
    {
        // Arrange — real time (waits must be real durations), 4 workers, capacity
        // far above the peak backlog so the admission watermarks never shed
        // (0.90 × 4096 = 3686 ≫ 600): every enqueued item must dispatch.
        const int producerCount = 4;
        const int itemsPerProducer = 150;
        var options = new WorkOrchestratorOptions
        {
            Capacity = 4096,
            WorkerCount = 4,
            DispatchStrategy = strategy,
        };
        options.Priority.InteractiveBoostWindow = TimeSpan.FromSeconds(30);
        options.Priority.BatchPenaltyWindow = TimeSpan.FromSeconds(30);

        var waits = new ConcurrentQueue<(WorkClass Class, TimeSpan Wait)>();
        await using var orchestrator = new WorkOrchestrator<string>(
            new DelayHandler(),
            Options.Create(options),
            NullLogger<WorkOrchestrator<string>>.Instance);
        orchestrator.QueueWaitObserved = (workClass, wait) => waits.Enqueue((workClass, wait));

        // Act — 4 concurrent producers enqueue mixed classes (deterministic
        // per-producer xorshift sequence: uniform-ish class spread with NO
        // systematic class-vs-enqueue-order correlation, so FIFO would show no
        // ordering) while the 4 workers consume concurrently.
        uint[] producerSeeds = [0x9E3779B9u, 0x85EBCA6Bu, 0xC2B2AE35u, 0x27D4EB2Fu];
        var producers = Enumerable.Range(0, producerCount)
            .Select(p => Task.Run(async () =>
            {
                var rejected = 0;
                var state = producerSeeds[p];
                for (var i = 0; i < itemsPerProducer; i++)
                {
                    var workClass = (WorkClass)(NextXorShift(ref state) % 3u);
                    var result = await orchestrator
                        .EnqueueAsync($"p{p}-i{i}", workClass)
                        .ConfigureAwait(false);
                    if (!result.IsAccepted)
                    {
                        rejected++;
                    }
                }

                return rejected;
            }))
            .ToArray();

        var rejectedCounts = await Task.WhenAll(producers).WaitAsync(WaitTimeout).ConfigureAwait(false);

        using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await orchestrator.DrainAsync(drainCts.Token).ConfigureAwait(false);

        // Assert — nothing was shed and everything dispatched exactly once.
        await Assert.That(rejectedCounts.Sum()).IsEqualTo(0);
        await Assert.That(waits.Count).IsEqualTo(producerCount * itemsPerProducer);

        var byClass = waits.GroupBy(w => w.Class)
            .ToDictionary(g => g.Key, g => g.Select(w => w.Wait.TotalMilliseconds).ToList());

        // Each class carries real statistical mass (deterministic seeds give ~200
        // per class; ≥ 100 guards the means against tiny-sample noise).
        await Assert.That(byClass[WorkClass.Interactive].Count).IsGreaterThanOrEqualTo(100);
        await Assert.That(byClass[WorkClass.Default].Count).IsGreaterThanOrEqualTo(100);
        await Assert.That(byClass[WorkClass.Batch].Count).IsGreaterThanOrEqualTo(100);

        var meanInteractive = byClass[WorkClass.Interactive].Average();
        var meanDefault = byClass[WorkClass.Default].Average();
        var meanBatch = byClass[WorkClass.Batch].Average();

        // Strict class ordering of mean dispatch wait, plus the separation floor
        // that noise cannot clear (see remarks).
        await Assert.That(meanInteractive).IsLessThan(meanDefault);
        await Assert.That(meanDefault).IsLessThan(meanBatch);
        await Assert.That(meanBatch - meanInteractive).IsGreaterThan(20.0);
    }

    /// <summary>
    /// Runs one gated-backlog trial through the public orchestrator API: a single
    /// worker is captured by a "gate" item held open in the handler; the batch
    /// backlog and then one interactive item are enqueued behind it on a frozen
    /// <see cref="FakeTimeProvider"/> (identical timestamps — the interactive
    /// boost is the strict global-minimum key); the gate is released and the queue
    /// drained.
    /// </summary>
    /// <param name="strategy">The dispatch strategy binding under test.</param>
    /// <returns>The complete dispatch order, beginning with the gate item.</returns>
    private static async Task<List<string>> RunGatedBacklogTrialAsync(DispatchStrategy strategy)
    {
        // Frozen clock: every envelope gets the same enqueue timestamp, so ordering
        // is decided purely by the class boost — determinism where it helps.
        var fakeTime = new FakeTimeProvider();
        var handler = new GateRecordingHandler();
        var options = new WorkOrchestratorOptions
        {
            // Capacity 256 keeps the 66-item trial far below the Batch admission
            // watermark (0.90 × 256 = 230): nothing is shed at admission.
            Capacity = 256,
            WorkerCount = 1,
            DispatchStrategy = strategy,
        };

        await using var orchestrator = new WorkOrchestrator<string>(
            handler,
            Options.Create(options),
            NullLogger<WorkOrchestrator<string>>.Instance,
            fakeTime);

        // Capture the single worker on the gate item.
        var gateAccepted = await orchestrator.EnqueueAsync("gate").ConfigureAwait(false);
        await Assert.That(gateAccepted).IsEqualTo(EnqueueResult.Accepted);
        await handler.GateEntered.WaitAsync(WaitTimeout).ConfigureAwait(false);

        // The criterion's arrangement: N batch items first, THEN the interactive item.
        for (var i = 0; i < BatchBacklog; i++)
        {
            var accepted = await orchestrator.EnqueueAsync($"batch-{i:D2}", WorkClass.Batch).ConfigureAwait(false);
            await Assert.That(accepted).IsEqualTo(EnqueueResult.Accepted);
        }

        var interactiveAccepted = await orchestrator.EnqueueAsync("interactive", WorkClass.Interactive).ConfigureAwait(false);
        await Assert.That(interactiveAccepted).IsEqualTo(EnqueueResult.Accepted);

        // Release the worker and drain everything.
        handler.ReleaseGate();
        using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await orchestrator.DrainAsync(drainCts.Token).ConfigureAwait(false);

        return handler.Order;
    }

    /// <summary>
    /// Advances a xorshift32 state and returns the next value. Deterministic,
    /// allocation-free pseudo-randomness for class assignment — used instead of
    /// <see cref="Random"/> so producer sequences are reproducible across runs.
    /// </summary>
    /// <param name="state">The generator state; must be seeded nonzero.</param>
    /// <returns>The next pseudo-random value.</returns>
    private static uint NextXorShift(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    /// <summary>
    /// Records dispatch order and holds the "gate" item open until released, so a
    /// trial can build a backlog behind a captured worker.
    /// </summary>
    private sealed class GateRecordingHandler : IWorkHandler<string>
    {
        private readonly TaskCompletionSource _gateEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _order = [];
        private readonly Lock _sync = new();

        /// <summary>
        /// Gets a task that completes when the worker has entered the gate item's
        /// handler (the backlog can then be enqueued behind a captured worker).
        /// </summary>
        public Task GateEntered => _gateEntered.Task;

        /// <summary>
        /// Gets the dispatch order recorded so far. Read after the drain completes,
        /// when no worker is mutating it.
        /// </summary>
        public List<string> Order
        {
            get
            {
                lock (_sync)
                {
                    return [.. _order];
                }
            }
        }

        /// <summary>
        /// Releases the gate, letting the captured worker proceed to the backlog.
        /// </summary>
        public void ReleaseGate() => _release.TrySetResult();

        /// <inheritdoc/>
        public async ValueTask HandleAsync(string work, CancellationToken ct)
        {
            lock (_sync)
            {
                _order.Add(work);
            }

            if (work == "gate")
            {
                _gateEntered.TrySetResult();
                await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Simulates a small unit of work per item so a backlog forms under
    /// multi-producer load and queue waits become class-separable.
    /// </summary>
    private sealed class DelayHandler : IWorkHandler<string>
    {
        /// <inheritdoc/>
        public async ValueTask HandleAsync(string work, CancellationToken ct)
            => await Task.Delay(1, ct).ConfigureAwait(false);
    }
}
