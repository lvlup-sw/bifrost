// =============================================================================
// <copyright file="AgingBoundAcceptanceTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics;
using System.Numerics;

using Bifrost.Core;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

namespace Bifrost.Tests.Acceptance;

/// <summary>
/// Acceptance tests for the DR-5 aging bound (issue #17): a Batch item is not starved
/// beyond the configured bound under sustained Interactive load, on both priority
/// bindings.
/// </summary>
/// <remarks>
/// <para>
/// <b>The mechanism under test — starvation bounded BY CONSTRUCTION.</b> The
/// virtual-time key is <c>key = enqueueTicks − Boost(class)</c> (see
/// <c>PriorityKey</c>): a Batch item older than
/// <see cref="PriorityDispatchOptions.InteractiveBoostWindow"/> has a SMALLER key than
/// any freshly enqueued Interactive item, so it outranks them. The boost window IS the
/// starvation bound — no aging scans, no re-scoring — modulo the MultiQueue binding's
/// documented rank relaxation plus in-flight work.
/// </para>
/// <para>
/// <b>Per-binding observation discipline.</b> The locking binding dequeues the exact
/// minimum key, so its assertions are strict and deterministic. The MultiQueue binding
/// relaxes dequeue order (two-choice sampling over <c>n</c> sub-queues, where
/// <c>n = RoundUpToPowerOf2(4 × ProcessorCount)</c>, expected rank error
/// <c>(5/6)·n</c>), so its assertions are statistical with bounds derived from the
/// structure's sampling mechanics: when the Batch item is the global minimum AND the
/// top of its own sub-queue, each two-choice dequeue samples a uniform pair of distinct
/// sub-queues, so the probability of popping the Batch item is at least
/// <c>2/n</c> per dequeue (the pair contains its sub-queue with probability
/// <c>2/n</c>, and the global minimum wins every top comparison). Tail bounds below
/// follow from that geometric lower bound; each test documents its own arithmetic.
/// </para>
/// <para>
/// <b>Kill-probe evidence (non-vacuity).</b> Each scenario was probed once by setting
/// the boost window to <c>TimeSpan.FromDays(365)</c> (aging can never win within the
/// advanced fake time), confirming every assertion fails, then restoring. The probe
/// for the MultiQueue sustained-load scenario is also why its backlog target is
/// <c>8 × n</c>: at low occupancy the two-choice rule pops nearly arbitrarily when a
/// sampled partner sub-queue is empty (anti-starvation by noise, probability
/// <c>≈ e^−occupancy</c> per dequeue), which would mask a disabled aging mechanism; at
/// occupancy 8 that noise path has probability <c>≈ 3.4 × 10⁻⁴</c>, so a starved
/// Batch item demonstrably stays starved without the boost-window mechanism.
/// </para>
/// </remarks>
public sealed class AgingBoundAcceptanceTests
{
    /// <summary>
    /// The work-item payload marking the Batch item whose dispatch position is measured.
    /// </summary>
    private const string BatchMarker = "BATCH";

    /// <summary>
    /// The number of independent trials for the MultiQueue median-position scenario.
    /// </summary>
    private const int MedianTrialCount = 20;

    /// <summary>
    /// Lockstep round cutoff for <see cref="MeasureLockstepDispatchesBeforeBatchAsync"/>:
    /// generous headroom over the largest expected value (10) so a starved Batch item
    /// (the kill-probe configuration) terminates the loop and fails the equality
    /// assertions loudly instead of spinning forever.
    /// </summary>
    private const int LockstepRoundCutoff = 64;

    /// <summary>
    /// The controlled inter-arrival spacing of the Interactive stream in fake time:
    /// every arrival advances the <see cref="FakeTimeProvider"/> by exactly this much,
    /// making the boost-window ÷ spacing arithmetic exact regardless of real-time rates.
    /// </summary>
    private static readonly TimeSpan InterArrival = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// The small configured boost window for the sustained-load and configurability
    /// scenarios. 105 ms is deliberately NOT an integer multiple of
    /// <see cref="InterArrival"/> (105 / 10 = 10.5), so no Interactive arrival ever
    /// lands on an exact key tie with the Batch item — boundary outcomes stay
    /// deterministic. The mechanism scales linearly with the window; configurability is
    /// the point (DR-5).
    /// </summary>
    private static readonly TimeSpan SmallBoostWindow = TimeSpan.FromMilliseconds(105);

    /// <summary>
    /// Per-test ceiling for waits that are expected to complete promptly.
    /// </summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Real-time failsafe for the sustained-load producer loop, so a hung worker fails
    /// the test instead of hanging the run.
    /// </summary>
    private static readonly TimeSpan ProducerFailsafe = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The MultiQueue sub-queue count on this host, mirroring the structure's default
    /// sizing (<c>RoundUpToPowerOf2(4 × ProcessorCount)</c> — see
    /// <c>ConcurrentPriorityQueue</c>). The orchestrator's MultiQueue binding always
    /// constructs with that default, so the statistical bounds derive from this value.
    /// </summary>
    private static readonly int SubQueueCount =
        (int)BitOperations.RoundUpToPowerOf2((uint)(4 * Environment.ProcessorCount));

    /// <summary>
    /// Verifies the aging bound deterministically on the exact-ordering locking binding:
    /// a Batch item enqueued at T0, with the fake clock then advanced past the default
    /// 30 s <see cref="PriorityDispatchOptions.InteractiveBoostWindow"/>, STRICTLY
    /// outranks a freshly enqueued Interactive item — the consumer is started only
    /// after both items are queued (the gate), and the handler must observe the Batch
    /// item first.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// Key arithmetic: <c>key(Batch@T0) = T0</c>;
    /// <c>key(Interactive@T0+30s+1ms) = T0 + 1ms</c> — the aged Batch item has the
    /// strictly smaller key, and the locking binding dequeues the exact minimum, so the
    /// outcome is deterministic with no statistical margin.
    /// </remarks>
    [Test]
    public async Task Batch_OlderThanBoostWindow_DispatchesBeforeFreshInteractive_Deterministic()
    {
        // Arrange — no static workers: the "gate" is that no consumer exists until both
        // items are queued. Default priority options (30 s interactive boost window).
        var fakeTime = new FakeTimeProvider();
        var handler = new RecordingHandler(expectedCount: 2);
        await using var orchestrator = CreateOrchestrator(
            new WorkOrchestratorOptions
            {
                Capacity = 16,
                WorkerCount = 0,
                DispatchStrategy = DispatchStrategy.PriorityLocking,
            },
            fakeTime,
            handler);

        var batch = await orchestrator.EnqueueAsync("batch-old", WorkClass.Batch).ConfigureAwait(false);
        await Assert.That(batch).IsEqualTo(EnqueueResult.Accepted);

        // Age the Batch item past the boost window, then enqueue fresh Interactive work.
        fakeTime.Advance(TimeSpan.FromSeconds(30) + TimeSpan.FromMilliseconds(1));
        var interactive = await orchestrator.EnqueueAsync("interactive-fresh", WorkClass.Interactive).ConfigureAwait(false);
        await Assert.That(interactive).IsEqualTo(EnqueueResult.Accepted);

        // Act — release the gate: start the single consumer and let it drain both items.
        var workerFunc = orchestrator.CreateWorkerFunction();
        using var cts = new CancellationTokenSource();
        var workerTask = Task.Run(() => workerFunc("AgingWorker", cts.Token), cts.Token);

        await handler.DoneTask.WaitAsync(WaitTimeout).ConfigureAwait(false);
        var sequence = handler.Snapshot();

        // Assert — strict: the aged Batch item dispatches before the fresh Interactive.
        await Assert.That(sequence[0]).IsEqualTo("batch-old");
        await Assert.That(sequence[1]).IsEqualTo("interactive-fresh");

        // Cleanup
        await cts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAny(workerTask, Task.Delay(WaitTimeout)).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies the aging bound statistically on the relaxed MultiQueue binding: a
    /// Batch item aged past the boost window holds the global-minimum key against
    /// <c>6n</c> fresh Interactive items, so over <see cref="MedianTrialCount"/>
    /// independent trials the upper-median of its dispatch position must stay within
    /// <c>1.5n</c> (n = sub-queue count) — far ahead of the <c>6n</c> fresh items that
    /// would all outrank it if aging were inert.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// <para>
    /// <b>Bound derivation (honest, from the structure's sampling mechanics).</b> The
    /// aged Batch item is the global minimum and immediately the top of its sub-queue
    /// (every Interactive key is larger), so each dequeue pops it with probability at
    /// least <c>2/n</c> (see the class remarks). Per trial,
    /// <c>P(position &gt; 1.5n) ≤ (1 − 2/n)^{1.5n} ≈ e⁻³ ≈ 5%</c>. The upper median of
    /// 20 trials exceeds the bound only if at least 10 trials exceed it:
    /// <c>P ≈ C(20,10) · 0.05¹⁰ · 0.95¹⁰ ≈ 1 × 10⁻⁸</c> — negligible flake risk.
    /// </para>
    /// <para>
    /// <b>Why median, not per-trial assertion:</b> a single trial CAN legitimately land
    /// past <c>1.5n</c> (the relaxation is real); the median is robust to those
    /// excursions while still collapsing decisively when the mechanism is disabled
    /// (kill-probe: positions concentrate near <c>6n</c>, several multiples past the
    /// bound).
    /// </para>
    /// </remarks>
    [Test]
    public async Task Batch_OlderThanBoostWindow_OutranksFreshInteractive_MultiQueue_MedianPositionBounded()
    {
        // Arrange — statistical parameters derived from the host's sub-queue count.
        var n = SubQueueCount;
        var freshInteractiveCount = 6 * n;
        var positionBound = (3 * n) / 2;
        var positions = new List<int>(MedianTrialCount);

        for (var trial = 0; trial < MedianTrialCount; trial++)
        {
            var fakeTime = new FakeTimeProvider();
            await using var orchestrator = CreateOrchestrator(
                new WorkOrchestratorOptions
                {
                    Capacity = 8 * n,
                    WorkerCount = 0,
                    DispatchStrategy = DispatchStrategy.PriorityMultiQueue,
                },
                fakeTime);

            var batch = await orchestrator.EnqueueAsync(BatchMarker, WorkClass.Batch).ConfigureAwait(false);
            if (!batch.IsAccepted)
            {
                await Assert.That(batch).IsEqualTo(EnqueueResult.Accepted);
            }

            // Age the Batch item past the default 30 s window, then flood with fresh
            // Interactive items (all share one fresh timestamp; only relative keys matter).
            fakeTime.Advance(TimeSpan.FromSeconds(30) + TimeSpan.FromMilliseconds(1));
            for (var i = 0; i < freshInteractiveCount; i++)
            {
                var fill = await orchestrator.EnqueueAsync($"interactive-{i}", WorkClass.Interactive).ConfigureAwait(false);
                if (!fill.IsAccepted)
                {
                    await Assert.That(fill).IsEqualTo(EnqueueResult.Accepted);
                }
            }

            // Act — drain single-threaded (the same TryDequeue the worker loop uses;
            // single-threaded, a false return is authoritative-empty, never spurious)
            // and record the Batch item's dispatch position.
            var position = -1;
            for (var i = 0; i <= freshInteractiveCount; i++)
            {
                if (!orchestrator.TryReadEnvelope(out var envelope))
                {
                    break;
                }

                if (envelope.Class == WorkClass.Batch)
                {
                    position = i;
                    break;
                }
            }

            // The Batch item must surface within the drain (conservation).
            await Assert.That(position).IsGreaterThanOrEqualTo(0);
            positions.Add(position);
        }

        // Assert — upper median (11th smallest of 20) within the derived bound.
        positions.Sort();
        var upperMedian = positions[MedianTrialCount / 2];
        Console.WriteLine(
            $"[T26-DATA] multiqueue median: n={n} M={freshInteractiveCount} bound={positionBound} " +
            $"upperMedian={upperMedian} positions=[{string.Join(",", positions)}]");
        await Assert.That(upperMedian).IsLessThanOrEqualTo(positionBound);
    }

    /// <summary>
    /// Verifies the issue #17 aging acceptance criterion end-to-end on both bindings:
    /// with a worker processing continuously (small real work per item) and a constant
    /// Interactive arrival stream keeping the queue saturated below the admission
    /// watermark, a single Batch item dispatches within a bounded number of subsequent
    /// dispatches — the bound derived from boost window ÷ inter-arrival spacing plus a
    /// binding-specific rank-error margin.
    /// </summary>
    /// <param name="strategy">The priority dispatch binding under test.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// <para>
    /// <b>Bound derivation.</b> Every Interactive arrival advances the fake clock by
    /// Δ = 10 ms; the boost window is W = 105 ms. Dispatches that may legitimately
    /// precede the Batch item (enqueued at fake time T_B):
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///   Backlog: items queued at T_B — capped at the producer's backlog target B
    ///   (producer enqueues only while <c>PendingCount &lt; B</c>); all outrank the
    ///   Batch item (their keys are at least W older). Contributes B.
    ///   </description></item>
    ///   <item><description>
    ///   Boosted arrivals: Interactive items arriving at fake time t with
    ///   <c>t &lt; T_B + W</c> still outrank (key <c>t − W &lt; T_B</c>); at Δ spacing
    ///   that is at most ⌈W/Δ⌉ = 11 arrivals. Every later arrival has a key larger than
    ///   the Batch item's and can only precede it through rank-error noise (item 4).
    ///   This is the aging bound proper: boost window ÷ inter-arrival spacing.
    ///   </description></item>
    ///   <item><description>
    ///   Slop: at most one in-flight item plus one dispatch racing the pre-enqueue
    ///   snapshot (single worker) — +2; the MultiQueue's striped count is approximate
    ///   under two threads, so its backlog cap carries +2 more.
    ///   </description></item>
    ///   <item><description>
    ///   Rank-error margin (MultiQueue only): clearing the ~B/n backlog items sharing
    ///   the Batch item's sub-queue and then popping it proceeds at ≥ 2/n per dequeue
    ///   once eligible (class remarks); 12n covers that negative-binomial tail to
    ///   roughly the 10⁻⁵ level (dominant term: ~9 required sub-queue hits at 2/n each,
    ///   expectation ≈ 4.5n, Chernoff tail at 12n ≪ 10⁻⁵ with the Poisson spread of the
    ///   blocker count folded in). Locking: 0 — exact minimum, no rank error.
    ///   </description></item>
    /// </list>
    /// <para>
    /// Locking: B = 8, bound = 8 + 11 + 2 = 21. MultiQueue: B = 8n (occupancy 8 per
    /// sub-queue — required so the kill-probe is meaningful, see class remarks), bound
    /// = 8n + 11 + 4 + 12n = 20n + 15.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(DispatchStrategy.PriorityLocking)]
    [Arguments(DispatchStrategy.PriorityMultiQueue)]
    public async Task Batch_UnderSustainedInteractiveLoad_DispatchedWithinBoundedDispatches(DispatchStrategy strategy)
    {
        // Arrange — per-binding load shape and bound (derivation in the remarks).
        var n = SubQueueCount;
        var isMultiQueue = strategy == DispatchStrategy.PriorityMultiQueue;
        var backlogTarget = isMultiQueue ? 8 * n : 8;
        var capacity = isMultiQueue ? 10 * n : 1024; // backlog stays below the 0.90 Batch watermark
        var boostedArrivalAllowance = 11; // ⌈W/Δ⌉ = ⌈105/10⌉
        var rankErrorMargin = isMultiQueue ? (12 * n) + 2 : 0;
        var bound = backlogTarget + boostedArrivalAllowance + 2 + rankErrorMargin;
        var cutoff = bound + 16;

        var fakeTime = new FakeTimeProvider();
        var handler = new RecordingHandler(marker: BatchMarker, delayPerItem: true);
        await using var orchestrator = CreateOrchestrator(
            new WorkOrchestratorOptions
            {
                Capacity = capacity,
                WorkerCount = 1,
                DispatchStrategy = strategy,
                Priority = { InteractiveBoostWindow = SmallBoostWindow },
            },
            fakeTime,
            handler);

        var stopwatch = Stopwatch.StartNew();
        var arrivals = 0;

        // Phase 1 — establish sustained Interactive load: fill to the backlog target
        // while the worker drains continuously. Each arrival advances fake time by Δ.
        while (orchestrator.PendingCount < backlogTarget && stopwatch.Elapsed < ProducerFailsafe)
        {
            fakeTime.Advance(InterArrival);
            var fill = await orchestrator.EnqueueAsync($"interactive-{arrivals++}", WorkClass.Interactive).ConfigureAwait(false);
            if (!fill.IsAccepted)
            {
                await Assert.That(fill).IsEqualTo(EnqueueResult.Accepted);
            }
        }

        // Phase 2 — snapshot the dispatch count, then enqueue the one Batch item under
        // full backlog (count ≈ B, well below the 0.90 × capacity Batch watermark).
        var dispatchedBeforeBatch = handler.Count;
        fakeTime.Advance(InterArrival);
        var batch = await orchestrator.EnqueueAsync(BatchMarker, WorkClass.Batch).ConfigureAwait(false);
        await Assert.That(batch).IsEqualTo(EnqueueResult.Accepted);

        // Phase 3 — sustain the Interactive stream until the Batch item dispatches, the
        // dispatch cutoff proves starvation, or the real-time failsafe trips.
        while (!handler.MarkerSeenTask.IsCompleted
            && handler.Count - dispatchedBeforeBatch <= cutoff
            && stopwatch.Elapsed < ProducerFailsafe)
        {
            if (orchestrator.PendingCount < backlogTarget)
            {
                fakeTime.Advance(InterArrival);

                // Acceptance is structurally guaranteed here (count < B ≪ watermark);
                // a rejection would only shrink the load and is policed by the bound.
                _ = await orchestrator.EnqueueAsync($"interactive-{arrivals++}", WorkClass.Interactive).ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
            }
        }

        // Assert — the Batch item dispatched (not starved past the cutoff), within the
        // derived bound of subsequent dispatches.
        var sequence = handler.Snapshot();
        var batchIndex = Array.IndexOf(sequence, BatchMarker);
        await Assert.That(batchIndex).IsGreaterThanOrEqualTo(0);

        var dispatchesBetween = batchIndex - dispatchedBeforeBatch;
        Console.WriteLine(
            $"[T26-DATA] sustained {strategy}: n={n} B={backlogTarget} bound={bound} cutoff={cutoff} " +
            $"dispatchesBetween={dispatchesBetween} arrivals={arrivals} " +
            $"totalDispatched={sequence.Length} elapsed={stopwatch.ElapsedMilliseconds}ms");
        await Assert.That(dispatchesBetween).IsLessThanOrEqualTo(bound);
    }

    /// <summary>
    /// Verifies that the boost window is the bound (DR-5 configurability): under an
    /// identical lockstep Interactive stream, halving
    /// <see cref="PriorityDispatchOptions.InteractiveBoostWindow"/> exactly halves the
    /// number of dispatches the Batch item waits.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// <para>
    /// <b>Derivation.</b> Lockstep round i enqueues one Interactive item at fake time
    /// <c>T0 + iΔ</c> (key <c>T0 + iΔ − W</c>) and dequeues once against the Batch item
    /// enqueued at T0 (key T0). The Interactive item wins while <c>iΔ &lt; W</c>, so
    /// the Batch item waits exactly <c>⌊W/Δ⌋</c> dispatches: 10 at W = 105 ms, 5 at
    /// W = 52.5 ms. Neither window is an integer multiple of Δ, so no exact key tie
    /// exists and the equalities are deterministic.
    /// </para>
    /// <para>
    /// <b>Binding choice (documented per task):</b> locking only. The lockstep queue
    /// holds at most two items, an occupancy where the MultiQueue's relaxation noise
    /// (empty-partner sampling and the index-ordered verification scan) dominates the
    /// windowing signal entirely. The key arithmetic under test (<c>PriorityKey</c>) is
    /// shared verbatim by both bindings, and the MultiQueue's realization of it is
    /// covered statistically by the other two scenarios.
    /// </para>
    /// </remarks>
    [Test]
    public async Task BoostWindow_Configurable_ChangesTheBound()
    {
        // Act — identical lockstep stream, full window then half window.
        var dispatchesAtFullWindow = await MeasureLockstepDispatchesBeforeBatchAsync(SmallBoostWindow).ConfigureAwait(false);
        var dispatchesAtHalfWindow = await MeasureLockstepDispatchesBeforeBatchAsync(SmallBoostWindow / 2).ConfigureAwait(false);

        // Assert — the observed bound is ⌊W/Δ⌋ and halves with the window.
        Console.WriteLine($"[T26-DATA] lockstep: full={dispatchesAtFullWindow} half={dispatchesAtHalfWindow}");
        await Assert.That(dispatchesAtFullWindow).IsEqualTo(10);
        await Assert.That(dispatchesAtHalfWindow).IsEqualTo(5);
        await Assert.That(dispatchesAtFullWindow).IsEqualTo(2 * dispatchesAtHalfWindow);
    }

    /// <summary>
    /// Runs the lockstep aging measurement on the exact-ordering locking binding: a
    /// Batch item is enqueued at T0, then each round advances fake time by
    /// <see cref="InterArrival"/>, enqueues one fresh Interactive item, and dequeues
    /// once — returning how many dispatches preceded the Batch item.
    /// </summary>
    /// <param name="boostWindow">The Interactive boost window to configure.</param>
    /// <returns>
    /// The number of dispatches that preceded the Batch item, or
    /// <see cref="LockstepRoundCutoff"/> when it never surfaced (starvation —
    /// surfaced loudly by the caller's equality assertions).
    /// </returns>
    private static async Task<int> MeasureLockstepDispatchesBeforeBatchAsync(TimeSpan boostWindow)
    {
        var fakeTime = new FakeTimeProvider();
        await using var orchestrator = CreateOrchestrator(
            new WorkOrchestratorOptions
            {
                Capacity = 16,
                WorkerCount = 0,
                DispatchStrategy = DispatchStrategy.PriorityLocking,
                Priority = { InteractiveBoostWindow = boostWindow },
            },
            fakeTime);

        var batch = await orchestrator.EnqueueAsync(BatchMarker, WorkClass.Batch).ConfigureAwait(false);
        await Assert.That(batch).IsEqualTo(EnqueueResult.Accepted);

        for (var round = 1; round <= LockstepRoundCutoff; round++)
        {
            fakeTime.Advance(InterArrival);
            var fill = await orchestrator.EnqueueAsync($"interactive-{round}", WorkClass.Interactive).ConfigureAwait(false);
            if (!fill.IsAccepted)
            {
                await Assert.That(fill).IsEqualTo(EnqueueResult.Accepted);
            }

            if (!orchestrator.TryReadEnvelope(out var envelope))
            {
                break; // conservation failure — the cutoff return fails the caller's asserts
            }

            if (envelope.Class == WorkClass.Batch)
            {
                return round - 1;
            }
        }

        return LockstepRoundCutoff;
    }

    /// <summary>
    /// Creates a <see cref="WorkOrchestrator{TWork}"/> with the supplied options, time
    /// provider, and handler (a no-op substitute when omitted). Validation is bypassed
    /// by design via <see cref="Options.Create{TOptions}(TOptions)"/>, so WorkerCount 0
    /// is usable for gated/quiescent scenarios.
    /// </summary>
    /// <param name="options">The orchestrator options to construct with.</param>
    /// <param name="timeProvider">The time provider stamping enqueue timestamps.</param>
    /// <param name="handler">The work handler, or <c>null</c> for a no-op substitute.</param>
    /// <returns>The constructed orchestrator.</returns>
    private static WorkOrchestrator<string> CreateOrchestrator(
        WorkOrchestratorOptions options,
        TimeProvider timeProvider,
        IWorkHandler<string>? handler = null)
        => new(
            handler ?? Substitute.For<IWorkHandler<string>>(),
            Options.Create(options),
            NullLogger<WorkOrchestrator<string>>.Instance,
            timeProvider);

    /// <summary>
    /// Handler that records the dispatch sequence (at entry, so order equals dequeue
    /// order under a single worker), optionally completing <see cref="DoneTask"/> at an
    /// expected count and <see cref="MarkerSeenTask"/> on a marker item, and optionally
    /// performing a small real delay per item (the sustained-load "small real work").
    /// </summary>
    private sealed class RecordingHandler : IWorkHandler<string>
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _markerSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _sequence = [];
        private readonly Lock _gate = new();
        private readonly int _expectedCount;
        private readonly string? _marker;
        private readonly bool _delayPerItem;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordingHandler"/> class.
        /// </summary>
        /// <param name="expectedCount">
        /// The handled-item count that completes <see cref="DoneTask"/>, or -1 to disable.
        /// </param>
        /// <param name="marker">The item that completes <see cref="MarkerSeenTask"/>, or <c>null</c>.</param>
        /// <param name="delayPerItem">Whether to perform ~1 ms of real work per item.</param>
        public RecordingHandler(int expectedCount = -1, string? marker = null, bool delayPerItem = false)
        {
            _expectedCount = expectedCount;
            _marker = marker;
            _delayPerItem = delayPerItem;
        }

        /// <summary>Gets a task that completes when the expected item count is reached.</summary>
        public Task DoneTask => _done.Task;

        /// <summary>Gets a task that completes when the marker item is dispatched.</summary>
        public Task MarkerSeenTask => _markerSeen.Task;

        /// <summary>Gets the number of items recorded so far.</summary>
        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _sequence.Count;
                }
            }
        }

        /// <inheritdoc/>
        public async ValueTask HandleAsync(string work, CancellationToken ct)
        {
            int recorded;
            lock (_gate)
            {
                _sequence.Add(work);
                recorded = _sequence.Count;
            }

            if (work == _marker)
            {
                _markerSeen.TrySetResult();
            }

            if (_expectedCount > 0 && recorded >= _expectedCount)
            {
                _done.TrySetResult();
            }

            if (_delayPerItem)
            {
                // Small real work: keeps the dispatch cadence slow enough that the
                // producer (µs-scale enqueues) reliably sustains the backlog target.
                await Task.Delay(TimeSpan.FromMilliseconds(1), ct).ConfigureAwait(false);
            }
        }

        /// <summary>Takes an ordered snapshot of the dispatch sequence.</summary>
        /// <returns>The items dispatched so far, in dispatch order.</returns>
        public string[] Snapshot()
        {
            lock (_gate)
            {
                return [.. _sequence];
            }
        }
    }
}
