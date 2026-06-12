// =============================================================================
// <copyright file="SoakRunner.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Queues;

namespace Bifrost.Benchmarks.Concurrency;

/// <summary>
/// Executes one consumer-shaped soak run (design DR-8) against a single priority binding:
/// class-mixed Poisson-ish producers and N async consumers drive the real
/// <c>IWorkQueue&lt;WorkEnvelope&lt;int&gt;&gt;</c> binding (reached via
/// <c>InternalsVisibleTo</c>), with Task.Delay-simulated work — the soak measures QUEUE
/// behavior in Bifrost's actual regime (few workers, seconds-long items, low contention),
/// not CPU throughput.
/// </summary>
/// <remarks>
/// <para>
/// <b>Arrival process.</b> Each class has a dedicated producer drawing exponential
/// inter-arrival gaps at <c>share × totalRate</c> — a Poisson-ish process. A pure open-loop
/// Poisson rate cannot hold a finite queue at mid occupancy (it either drains to empty or
/// saturates at the watermark), so the total rate is steered by a slow feedback controller:
/// every monitor tick, the rate is nudged up when sampled occupancy is below the target band
/// and down when above it. The inter-arrival draws stay exponential; only the rate parameter
/// drifts, slowly. Periodic Batch bursts (back-to-back enqueues) deliberately push the queue
/// from the band into watermark territory so admission shedding (DR-6) is exercised.
/// </para>
/// <para>
/// <b>Measurement.</b> Queue wait is the envelope-timestamp pattern: producers stamp
/// <c>WorkEnvelope.EnqueuedAtTicks</c> via <see cref="TimeProvider.GetTimestamp"/>, and a
/// worker computes the wait at dequeue with <see cref="TimeProvider.GetElapsedTime(long)"/>
/// — exactly how the orchestrator's own queue-wait histogram works. Allocation stability is
/// process-wide (<see cref="GC.GetTotalAllocatedBytes(bool)"/> deltas), so it includes
/// harness overhead (timers, list growth); the signal is the FLATNESS of the per-interval
/// rate, not its absolute value.
/// </para>
/// <para>
/// <b>Starvation probe.</b> The DR-5 bound is on priority-induced overtaking: an item that
/// has waited longer than the Interactive boost window outranks every fresh arrival, so only
/// Interactive items enqueued within the window after a Batch item can overtake it. The
/// absolute wait of an admitted Batch item is therefore bounded by the boost window PLUS the
/// drain time of a full queue (<c>capacity × meanWork / workers</c>). The probe records the
/// maximum observed Batch wait and checks it against that depth-adjusted ceiling, reporting
/// the raw boost window alongside for context.
/// </para>
/// </remarks>
internal sealed class SoakRunner
{
    /// <summary>The monitor cadence: occupancy sampling and controller adjustment period, in seconds.</summary>
    private const double MonitorTickSeconds = 0.5;

    /// <summary>GC snapshots are taken every this many monitor ticks (= every 5 s at the 0.5 s tick).</summary>
    private const int GcSampleEveryTicks = 10;

    /// <summary>The multiplicative rate nudge applied when occupancy is below the target band.</summary>
    private const double RateGainUp = 1.10;

    /// <summary>The multiplicative rate nudge applied when occupancy is above the target band (stronger, to curb ramp overshoot).</summary>
    private const double RateGainDown = 0.85;

    /// <summary>The initial total arrival rate, as a multiple of the saturation rate — high so the queue fills into the band quickly.</summary>
    private const double InitialRateMultiple = 4.0;

    /// <summary>The controller's lower rate clamp, as a multiple of the saturation rate.</summary>
    private const double MinRateMultiple = 0.1;

    /// <summary>The controller's upper rate clamp, as a multiple of the saturation rate.</summary>
    private const double MaxRateMultiple = 8.0;

    /// <summary>The ceiling on any single exponential inter-arrival gap, so a deep-tail draw cannot stall a producer past the window.</summary>
    private const double MaxArrivalGapSeconds = 10.0;

    private readonly SoakBinding _binding;
    private readonly int _workers;
    private readonly SoakConfig _config;
    private readonly TimeProvider _time = TimeProvider.System;
    private readonly SoakQueue _queue;
    private readonly ClassCollector[] _collectors;
    private readonly RateBox _rate = new();
    private readonly double _saturationRatePerSecond;
    private readonly List<GcSample> _gcSamples = new(capacity: 256);

    /// <summary>The run's start timestamp (<see cref="TimeProvider.GetTimestamp"/> units), set once in <see cref="RunAsync"/>.</summary>
    private long _startTimestamp;

    // Occupancy accumulators — written only by the single monitor task, read after it joins.
    private long _occupancySamples;
    private double _occupancySum;
    private double _occupancyMax;
    private long _occupancyInBand;

    /// <summary>
    /// Initializes a new instance of the <see cref="SoakRunner"/> class for one
    /// binding × worker-count run.
    /// </summary>
    /// <param name="binding">The priority binding to drive.</param>
    /// <param name="workers">The number of concurrent consumers; must be at least one.</param>
    /// <param name="config">The workload shape.</param>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="workers"/> is less than one.</exception>
    internal SoakRunner(SoakBinding binding, int workers, SoakConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);

        _binding = binding;
        _workers = workers;
        _config = config;
        _queue = CreateQueue(binding, config, _time);

        _collectors = new ClassCollector[3];
        for (int i = 0; i < _collectors.Length; i++)
        {
            _collectors[i] = new ClassCollector();
        }

        // Saturation: the consumers' aggregate service rate. The controller steers the total
        // arrival rate around this to hold the occupancy band.
        _saturationRatePerSecond = workers / config.MeanWorkSeconds;
        _rate.RatePerSecond = InitialRateMultiple * _saturationRatePerSecond;
    }

    /// <summary>
    /// Runs the soak window: spawns the monitor, the three class producers, the burst task,
    /// and the consumers; sleeps the window; cancels everything; and aggregates the result.
    /// Residual queue content is reported, not drained — the window measures steady state.
    /// </summary>
    /// <returns>The aggregated <see cref="SoakResult"/>.</returns>
    internal async Task<SoakResult> RunAsync()
    {
        _startTimestamp = _time.GetTimestamp();
        RecordGcSample();

        using var cts = new CancellationTokenSource();
        CancellationToken ct = cts.Token;

        var tasks = new List<Task>
        {
            Task.Run(() => MonitorAsync(ct)),
            Task.Run(() => ProduceAsync(WorkClass.Interactive, _config.InteractiveShare, unchecked((_config.Seed * 31) + 1), ct)),
            Task.Run(() => ProduceAsync(WorkClass.Default, _config.DefaultShare, unchecked((_config.Seed * 31) + 2), ct)),
            Task.Run(() => ProduceAsync(WorkClass.Batch, _config.BatchShare, unchecked((_config.Seed * 31) + 3), ct)),
            Task.Run(() => BurstAsync(unchecked((_config.Seed * 31) + 4), ct)),
        };

        for (int w = 0; w < _workers; w++)
        {
            tasks.Add(Task.Run(() => ConsumeAsync(ct)));
        }

        await Task.Delay(TimeSpan.FromSeconds(_config.DurationSeconds)).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(tasks).ConfigureAwait(false);

        _queue.Complete();
        RecordGcSample();

        return BuildResult();
    }

    /// <summary>Nearest-rank percentile over an ascending-sorted sample array; 0 when empty.</summary>
    /// <param name="sorted">The ascending-sorted samples.</param>
    /// <param name="percentile">The percentile in <c>(0, 100]</c>.</param>
    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        int rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }

    /// <summary>
    /// Creates the requested internal binding (via <c>InternalsVisibleTo</c>) behind the
    /// uniform <see cref="SoakQueue"/> driver surface, with the DR-6 default watermarks
    /// (0.90 / 0.95 / 1.0) and the configured Interactive boost window.
    /// </summary>
    /// <param name="binding">The binding to construct.</param>
    /// <param name="config">The workload shape supplying capacity and boost window.</param>
    /// <param name="time">The time provider whose timestamp frequency the binding's key precompute needs.</param>
    private static SoakQueue CreateQueue(SoakBinding binding, SoakConfig config, TimeProvider time)
    {
        var options = new PriorityDispatchOptions
        {
            InteractiveBoostWindow = TimeSpan.FromSeconds(config.InteractiveBoostSeconds),
        };

        switch (binding)
        {
            case SoakBinding.MultiQueuePriority:
            {
                var queue = new ConcurrentPriorityWorkQueue<int>(config.Capacity, options, time.TimestampFrequency);
                return new SoakQueue(queue, queue.Complete);
            }

            case SoakBinding.LockingPriority:
            {
                var queue = new LockingPriorityWorkQueue<int>(config.Capacity, options, time.TimestampFrequency);
                return new SoakQueue(queue, queue.Complete);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(binding), binding, "Unknown soak binding.");
        }
    }

    /// <summary>
    /// One class's producer loop: exponential inter-arrival gaps at <c>share × totalRate</c>
    /// (re-reading the controller's rate each iteration), each arrival a log-uniform-duration
    /// envelope offered to the binding. Runs until the window's cancellation.
    /// </summary>
    /// <param name="workClass">The class this producer offers.</param>
    /// <param name="share">This class's fraction of the total arrival rate.</param>
    /// <param name="seed">The deterministic RNG seed for this producer's stream.</param>
    /// <param name="ct">The soak-window token.</param>
    private async Task ProduceAsync(WorkClass workClass, double share, int seed, CancellationToken ct)
    {
        if (share <= 0)
        {
            return;
        }

        var rng = new Random(seed);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                double rate = Math.Max(0.05, _rate.RatePerSecond * share);
                double gapSeconds = Math.Min(-Math.Log(1.0 - rng.NextDouble()) / rate, MaxArrivalGapSeconds);
                await Task.Delay(TimeSpan.FromSeconds(gapSeconds), ct).ConfigureAwait(false);

                EnqueueOnce(workClass, DrawWorkMs(rng));
            }
        }
        catch (OperationCanceledException)
        {
            // Orderly end of the soak window.
        }
    }

    /// <summary>
    /// The periodic Batch burst: every burst interval, offers <c>BurstSize</c> Batch envelopes
    /// back-to-back — sized to push a mid-band queue into watermark territory so the DR-6
    /// admission shed (Batch first) is exercised under otherwise steady load.
    /// </summary>
    /// <param name="seed">The deterministic RNG seed for the burst durations.</param>
    /// <param name="ct">The soak-window token.</param>
    private async Task BurstAsync(int seed, CancellationToken ct)
    {
        if (_config.BurstSize <= 0 || _config.BurstIntervalSeconds <= 0)
        {
            return;
        }

        var rng = new Random(seed);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(_config.BurstIntervalSeconds), ct).ConfigureAwait(false);

                for (int i = 0; i < _config.BurstSize; i++)
                {
                    EnqueueOnce(WorkClass.Batch, DrawWorkMs(rng));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Orderly end of the soak window.
        }
    }

    /// <summary>
    /// One consumer's canonical consume loop: wait, try-dequeue (tolerating relaxed-binding
    /// spurious misses by looping back), record the queue wait, then simulate the item's work
    /// with <see cref="Task.Delay(int, CancellationToken)"/>. Exits when the wait reports
    /// shutdown or cancellation interrupts a simulated item.
    /// </summary>
    /// <param name="ct">The soak-window token.</param>
    private async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            while (await _queue.WaitToDequeueAsync(ct).ConfigureAwait(false))
            {
                if (!_queue.TryDequeue(out WorkEnvelope<int> envelope))
                {
                    // Canonical loop: a miss is never an error — loop back to the wait.
                    continue;
                }

                double waitMs = _time.GetElapsedTime(envelope.EnqueuedAtTicks).TotalMilliseconds;
                _collectors[(int)envelope.Class].RecordDispatch(waitMs);

                // Simulated work: the soak measures queue behavior, not CPU.
                await Task.Delay(envelope.Work, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Window closed mid-item; its queue wait was already recorded at dequeue.
        }
    }

    /// <summary>
    /// The monitor loop: every tick, samples occupancy (band accounting), nudges the arrival
    /// controller toward the band, and periodically snapshots GC counters for the
    /// allocation-stability series.
    /// </summary>
    /// <param name="ct">The soak-window token.</param>
    private async Task MonitorAsync(CancellationToken ct)
    {
        int tick = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(MonitorTickSeconds), ct).ConfigureAwait(false);
                tick++;

                double occupancy = (double)_queue.Count / _config.Capacity;
                _occupancySamples++;
                _occupancySum += occupancy;
                if (occupancy > _occupancyMax)
                {
                    _occupancyMax = occupancy;
                }

                if (occupancy >= _config.TargetOccupancyLow && occupancy <= _config.TargetOccupancyHigh)
                {
                    _occupancyInBand++;
                }

                // Slow feedback: nudge the total arrival rate toward the band; inside it, hold.
                double rate = _rate.RatePerSecond;
                if (occupancy < _config.TargetOccupancyLow)
                {
                    rate *= RateGainUp;
                }
                else if (occupancy > _config.TargetOccupancyHigh)
                {
                    rate *= RateGainDown;
                }

                _rate.RatePerSecond = Math.Clamp(
                    rate,
                    MinRateMultiple * _saturationRatePerSecond,
                    MaxRateMultiple * _saturationRatePerSecond);

                if (tick % GcSampleEveryTicks == 0)
                {
                    RecordGcSample();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Orderly end of the soak window.
        }
    }

    /// <summary>Offers one envelope (stamped now) to the binding, counting the attempt and any rejection for its class.</summary>
    /// <param name="workClass">The class to offer under.</param>
    /// <param name="workMs">The simulated duration carried as the envelope payload.</param>
    private void EnqueueOnce(WorkClass workClass, int workMs)
    {
        var envelope = new WorkEnvelope<int>(workMs, workClass, _time.GetTimestamp());
        bool accepted = _queue.TryEnqueue(in envelope);
        _collectors[(int)workClass].RecordOffer(accepted);
    }

    /// <summary>Draws a log-uniform simulated duration in <c>[MinWorkMs, MaxWorkMs]</c> milliseconds.</summary>
    /// <param name="rng">The producer's RNG stream.</param>
    private int DrawWorkMs(Random rng)
    {
        double logSpan = Math.Log((double)_config.MaxWorkMs / _config.MinWorkMs);
        return (int)Math.Round(_config.MinWorkMs * Math.Exp(rng.NextDouble() * logSpan));
    }

    /// <summary>Appends one GC snapshot (elapsed seconds, total allocated bytes, gen-0/1/2 counts) to the stability series.</summary>
    private void RecordGcSample()
        => _gcSamples.Add(new GcSample(
            _time.GetElapsedTime(_startTimestamp).TotalSeconds,
            GC.GetTotalAllocatedBytes(precise: false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2)));

    /// <summary>Aggregates the collectors, occupancy accumulators, and GC series into the run's <see cref="SoakResult"/>.</summary>
    private SoakResult BuildResult()
    {
        // Per-class totals first, so shares have denominators.
        var snapshots = new (long Offered, long Rejected, double[] SortedWaits, double MaxWaitMs)[3];
        long totalOffered = 0;
        long totalDispatched = 0;
        for (int i = 0; i < 3; i++)
        {
            var (offered, rejected, sortedWaits, maxWaitMs) = _collectors[i].Snapshot();
            snapshots[i] = (offered, rejected, sortedWaits, maxWaitMs);
            totalOffered += offered;
            totalDispatched += sortedWaits.Length;
        }

        var classes = new List<SoakClassResult>(3);
        foreach (WorkClass workClass in (ReadOnlySpan<WorkClass>)[WorkClass.Interactive, WorkClass.Default, WorkClass.Batch])
        {
            var (offered, rejected, sortedWaits, maxWaitMs) = snapshots[(int)workClass];
            classes.Add(new SoakClassResult(
                workClass,
                offered,
                rejected,
                sortedWaits.Length,
                totalOffered > 0 ? (double)offered / totalOffered : 0,
                totalDispatched > 0 ? (double)sortedWaits.Length / totalDispatched : 0,
                Percentile(sortedWaits, 50),
                Percentile(sortedWaits, 95),
                Percentile(sortedWaits, 99),
                maxWaitMs));
        }

        // Allocation stability: per-interval rates between consecutive GC snapshots, the first
        // (start-up) interval excluded when there are enough samples to afford it.
        double allocMean = 0;
        double allocMin = 0;
        double allocMax = 0;
        if (_gcSamples.Count >= 2)
        {
            var rates = new List<double>(_gcSamples.Count);
            int firstInterval = _gcSamples.Count > 3 ? 1 : 0;
            for (int i = firstInterval; i < _gcSamples.Count - 1; i++)
            {
                double dt = _gcSamples[i + 1].ElapsedSeconds - _gcSamples[i].ElapsedSeconds;
                if (dt > 0)
                {
                    rates.Add((_gcSamples[i + 1].AllocatedBytes - _gcSamples[i].AllocatedBytes) / dt * 60.0);
                }
            }

            if (rates.Count > 0)
            {
                allocMean = rates.Average();
                allocMin = rates.Min();
                allocMax = rates.Max();
            }
        }

        GcSample first = _gcSamples.Count > 0 ? _gcSamples[0] : default;
        GcSample last = _gcSamples.Count > 0 ? _gcSamples[^1] : default;

        double maxBatchWaitSeconds = snapshots[(int)WorkClass.Batch].MaxWaitMs / 1000.0;
        double depthAdjustedBoundSeconds =
            _config.InteractiveBoostSeconds + (_config.Capacity * _config.MeanWorkSeconds / _workers);

        return new SoakResult(
            _binding,
            _workers,
            _config.DurationSeconds,
            _config.Capacity,
            classes,
            _queue.Count,
            _occupancySamples > 0 ? _occupancySum / _occupancySamples * 100.0 : 0,
            _occupancyMax * 100.0,
            _occupancySamples > 0 ? (double)_occupancyInBand / _occupancySamples * 100.0 : 0,
            allocMean,
            allocMin,
            allocMax,
            last.Gen0 - first.Gen0,
            last.Gen1 - first.Gen1,
            last.Gen2 - first.Gen2,
            maxBatchWaitSeconds,
            _config.InteractiveBoostSeconds,
            depthAdjustedBoundSeconds,
            maxBatchWaitSeconds <= depthAdjustedBoundSeconds);
    }

    /// <summary>
    /// The uniform driver surface over both internal bindings: forwards the
    /// <c>IWorkQueue</c> members and reaches the concrete-only <c>Complete</c> through the
    /// delegate captured at construction (mirroring how the orchestrator reaches it via the
    /// concrete type).
    /// </summary>
    /// <param name="inner">The binding under soak.</param>
    /// <param name="complete">The binding's concrete <c>Complete</c> method.</param>
    private sealed class SoakQueue(IWorkQueue<WorkEnvelope<int>> inner, Action complete)
    {
        /// <summary>Gets the binding's current item count (approximate under the relaxed binding).</summary>
        public int Count => inner.Count;

        /// <summary>Offers one envelope; <c>false</c> is a watermark or capacity rejection.</summary>
        /// <param name="item">The envelope to offer.</param>
        public bool TryEnqueue(in WorkEnvelope<int> item) => inner.TryEnqueue(in item);

        /// <summary>Waits until an item is likely available, or <c>false</c> on shutdown/cancellation.</summary>
        /// <param name="cancellationToken">The soak-window token.</param>
        public ValueTask<bool> WaitToDequeueAsync(CancellationToken cancellationToken)
            => inner.WaitToDequeueAsync(cancellationToken);

        /// <summary>Attempts a dequeue; relaxed bindings may miss spuriously.</summary>
        /// <param name="item">The dequeued envelope on <c>true</c>.</param>
        public bool TryDequeue(out WorkEnvelope<int> item) => inner.TryDequeue(out item);

        /// <summary>Marks the binding complete for adding (teardown hygiene).</summary>
        public void Complete() => complete();
    }

    /// <summary>
    /// One class's thread-safe accumulator: offer/rejection counters (interlocked — Batch has
    /// two writers, its producer and the burst task) and the dispatch-wait samples plus max
    /// (under a lock; at soak rates of a few dispatches per second, contention is nil).
    /// </summary>
    private sealed class ClassCollector
    {
        private readonly Lock _lock = new();
        private readonly List<double> _waitsMs = new(capacity: 1 << 14);
        private long _offered;
        private long _rejected;
        private double _maxWaitMs;

        /// <summary>Counts one enqueue attempt and, when refused, one rejection.</summary>
        /// <param name="accepted">Whether the binding accepted the envelope.</param>
        public void RecordOffer(bool accepted)
        {
            Interlocked.Increment(ref _offered);
            if (!accepted)
            {
                Interlocked.Increment(ref _rejected);
            }
        }

        /// <summary>Records one dispatched item's queue wait.</summary>
        /// <param name="waitMs">The queue wait in milliseconds.</param>
        public void RecordDispatch(double waitMs)
        {
            lock (_lock)
            {
                _waitsMs.Add(waitMs);
                if (waitMs > _maxWaitMs)
                {
                    _maxWaitMs = waitMs;
                }
            }
        }

        /// <summary>Snapshots the counters and the ascending-sorted wait samples.</summary>
        public (long Offered, long Rejected, double[] SortedWaits, double MaxWaitMs) Snapshot()
        {
            lock (_lock)
            {
                double[] sorted = _waitsMs.ToArray();
                Array.Sort(sorted);
                return (Interlocked.Read(ref _offered), Interlocked.Read(ref _rejected), sorted, _maxWaitMs);
            }
        }
    }

    /// <summary>
    /// The controller's shared total-arrival-rate cell. <c>volatile double</c> is not legal
    /// C#, so the value is stored as its bit pattern in a <see cref="long"/> and accessed
    /// with <see cref="Volatile"/> reads/writes — tear-free across the producer and monitor
    /// tasks.
    /// </summary>
    private sealed class RateBox
    {
        private long _bits;

        /// <summary>Gets or sets the total arrival rate, in items per second.</summary>
        public double RatePerSecond
        {
            get => BitConverter.Int64BitsToDouble(Volatile.Read(ref _bits));
            set => Volatile.Write(ref _bits, BitConverter.DoubleToInt64Bits(value));
        }
    }

    /// <summary>One point in the allocation-stability series.</summary>
    /// <param name="ElapsedSeconds">Seconds since the run started.</param>
    /// <param name="AllocatedBytes">Process-wide total allocated bytes (<see cref="GC.GetTotalAllocatedBytes(bool)"/>, imprecise mode).</param>
    /// <param name="Gen0">Cumulative gen-0 collection count.</param>
    /// <param name="Gen1">Cumulative gen-1 collection count.</param>
    /// <param name="Gen2">Cumulative gen-2 collection count.</param>
    private readonly record struct GcSample(double ElapsedSeconds, long AllocatedBytes, int Gen0, int Gen1, int Gen2);
}
