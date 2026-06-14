// =============================================================================
// <copyright file="ThroughputRunner.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================
// Ported from lvlup-sw/DataFerry@2bf0456 (src/DataFerry.Benchmarks/Throughput/ThroughputRunner.cs);
// NaiveBaseline target renamed to LockingBaseline per design DR-1.

using System.Diagnostics;

namespace Bifrost.Benchmarks.Concurrency;

/// <summary>
/// The fixed-window multithreaded throughput harness. BenchmarkDotNet is deliberately <b>not</b>
/// used for cross-thread throughput — the BCL's own equivalent benchmark is disabled for
/// instability — so this custom runner measures it instead: dedicated worker
/// <see cref="Thread"/>s are parked on a <see cref="Barrier"/>, released simultaneously, run their
/// workload loop against a <i>shared</i> queue until a wall-clock window elapses, and their padded
/// per-thread operation counters are summed into a <see cref="ThroughputResult"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Barrier start.</b> Every worker plus the controller signal one <see cref="Barrier"/>, so no
/// thread begins its loop until all are spun up — eliminating the ramp-up skew where early threads
/// run uncontended while the rest are still being created. The controller participates so it can
/// start the wall-clock timer at the exact release instant.
/// </para>
/// <para>
/// <b>Padded per-thread counters (the 128-byte rationale).</b> Each worker increments its own slot
/// in a shared <see cref="long"/> array. If those slots shared a cache line, every increment by one
/// worker would invalidate the line in every other core's cache — <b>false sharing</b> that would
/// make the harness measure cache-coherence traffic instead of queue throughput. So each worker's
/// counter is placed on its own 128-byte cache line, exactly mirroring <c>SubQueueHeader</c>: an x64
/// cache line is 64 bytes, but the adjacent-line prefetcher fetches 128-byte-aligned pairs, so 128
/// bytes is the BCL-proven (<c>PaddedHeadAndTail</c>) false-sharing unit. With 8 bytes per
/// <see cref="long"/>, a 16-<see cref="long"/> stride is 128 bytes, so slot <c>t</c> lives at index
/// <c>t * <see cref="CounterStride"/></c>. After the join the padded slots are gathered back into a
/// compact <c>long[threadCount]</c> for the result.
/// </para>
/// <para>
/// <b>Stop signal.</b> A <c>volatile bool stop</c> flips after the window; each loop re-reads it
/// every iteration, so the threads exit promptly without coordination beyond that single flag (no
/// lock on the hot path).
/// </para>
/// </remarks>
public sealed class ThroughputRunner
{
    /// <summary>
    /// The number of <see cref="long"/> elements between adjacent per-thread counter slots. 16
    /// longs × 8 bytes = 128 bytes, isolating each counter on its own cache-line pair so the workers'
    /// increments never false-share (the same 128-byte unit <c>SubQueueHeader</c> pads to).
    /// </summary>
    internal const int CounterStride = 16;

    /// <summary>The fixed total element count the <see cref="ThroughputWorkload.Drain"/> workload pre-populates, split evenly across threads.</summary>
    internal const int DrainTotalPopulation = 1_000_000;

    /// <summary>The number of distinct priority values the <see cref="ThroughputWorkload.NarrowKeyRange"/> workload draws from.</summary>
    internal const int NarrowKeyRange = 1_000;

    /// <summary>
    /// The steady-state population pre-filled before the timed window for every non-drain workload,
    /// so mixed measurements exercise the two-choice fast path instead of the empty-queue
    /// verification scan (see the pre-population comment in <see cref="Run"/>).
    /// </summary>
    internal const int MixedPrepopulation = 100_000;

    /// <summary>The default fixed wall-clock window for a production run.</summary>
    public static TimeSpan DefaultWindow => TimeSpan.FromSeconds(3);

    /// <summary>
    /// Runs one fixed-window throughput measurement against a freshly created shared queue.
    /// </summary>
    /// <param name="target">The queue implementation and removal semantics to exercise.</param>
    /// <param name="workload">The cross-thread workload shape to run.</param>
    /// <param name="threadCount">The number of worker threads; must be at least one.</param>
    /// <param name="window">The fixed wall-clock window the workers run for.</param>
    /// <param name="drainPrepopulationPerThread">
    /// For <see cref="ThroughputWorkload.Drain"/> only: the number of elements each thread
    /// pre-populates before the timed drain. When <c>0</c> (the default) the workload uses its own
    /// <c>1_000_000 / threadCount</c> share so a sweep needs no extra wiring; tests pass a small value
    /// to keep the smoke run fast. Ignored by every non-drain workload.
    /// </param>
    /// <param name="stickiness">The stickiness factor <c>s</c> for the MultiQueue targets (inert for the locking baseline).</param>
    /// <returns>The aggregated <see cref="ThroughputResult"/> for the run.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="threadCount"/> is less than one.</exception>
    public ThroughputResult Run(
        ThroughputTarget target,
        ThroughputWorkload workload,
        int threadCount,
        TimeSpan window,
        int drainPrepopulationPerThread = 0,
        int stickiness = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threadCount, 1);

        IThroughputQueue queue = CreateQueue(target, stickiness);

        // Drain pre-populates the shared queue BEFORE the timed window opens so the window measures
        // pure drain throughput, not the fill.
        int drainPerThread = workload == ThroughputWorkload.Drain
            ? (drainPrepopulationPerThread > 0 ? drainPrepopulationPerThread : Math.Max(1, DrainTotalPopulation / threadCount))
            : 0;

        if (workload == ThroughputWorkload.Drain)
        {
            long fill = (long)drainPerThread * threadCount;
            for (long i = 0; i < fill; i++)
            {
                queue.Enqueue(i);
            }
        }
        else
        {
            // Mixed/split workloads pre-populate a steady-state population before the window opens
            // (the standard MultiQueue-literature methodology). A 1:1 op mix over an EMPTY queue
            // keeps the population near zero, which forces the relaxed dequeue's two-choice samples
            // to miss constantly and fall into the O(n) empty-verification scan — measuring the
            // empty-queue path, not mixed throughput. The fixed population keeps the dequeues on
            // the intended two-choice fast path while the 1:1 mix holds the population steady.
            for (long i = 0; i < MixedPrepopulation; i++)
            {
                queue.Enqueue(i);
            }
        }

        // Padded counters: one 128-byte-isolated slot per worker (see the class remarks).
        long[] paddedCounters = new long[threadCount * CounterStride];

        // The volatile stop flag is wrapped in a one-field box so a closure can flip it and every
        // worker re-reads the same volatile location each iteration.
        var stop = new StopFlag();

        // Workers + this controller all rendezvous on the barrier; the controller's participation
        // lets it start the timer at the exact release instant.
        using var barrier = new Barrier(threadCount + 1);

        var workers = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            int threadIndex = t;
            int counterSlot = t * CounterStride;
            int perThreadDrainCount = drainPerThread;

            var worker = new Thread(() =>
            {
                // Thread-seeded RNG: distinct streams per thread, deterministic per index for
                // reproducibility within a run, without cross-thread contention on Random.Shared.
                var rng = new Random(unchecked(0x5DEECE66 + (threadIndex * unchecked((int)0x9E3779B1))));

                barrier.SignalAndWait();

                long ops = RunWorkload(workload, queue, threadIndex, threadCount, perThreadDrainCount, rng, stop);

                paddedCounters[counterSlot] = ops;
            })
            {
                IsBackground = true,
                Name = $"throughput-{target}-{workload}-t{threadIndex}",
            };
            workers[t] = worker;
            worker.Start();
        }

        // Release all workers simultaneously, then time the window precisely from that instant.
        barrier.SignalAndWait();
        var sw = Stopwatch.StartNew();

        if (workload == ThroughputWorkload.Drain)
        {
            // Drain workers exit on their own when the queue is observed empty — usually well
            // before the window. Measure the ACTUAL elapsed drain time: dividing by the full
            // window would pin every target at population/window and erase the comparison. The
            // workers all drain the same shared queue concurrently, so the watchdog is a SINGLE
            // wall-clock window across all of them (not threadCount × window): a drain that never
            // empties is bounded by one window total, then fails fast rather than hanging.
            var drainWatchdog = Stopwatch.StartNew();
            foreach (Thread worker in workers)
            {
                TimeSpan remaining = window - drainWatchdog.Elapsed;
                if (remaining <= TimeSpan.Zero || !worker.Join(remaining))
                {
                    stop.Stop = true;
                    throw new TimeoutException(
                        $"Drain workload exceeded its {window.TotalSeconds:0.#}s watchdog window before all workers emptied the queue.");
                }
            }

            stop.Stop = true; // Watchdog: stop any straggler that never observed empty.
            sw.Stop();

            JoinAllOrThrow(workers);
        }
        else
        {
            // Sleep the window, then flip the stop flag; each worker's loop re-reads it and exits.
            Thread.Sleep(window);
            stop.Stop = true;
            sw.Stop();

            JoinAllOrThrow(workers);
        }

        // Gather the padded slots back into a compact, contiguous per-thread array for the result.
        long[] perThreadOps = new long[threadCount];
        long total = 0;
        for (int t = 0; t < threadCount; t++)
        {
            long ops = paddedCounters[t * CounterStride];
            perThreadOps[t] = ops;
            total += ops;
        }

        double seconds = sw.Elapsed.TotalSeconds;
        double opsPerSecond = seconds > 0 ? total / seconds : 0d;

        return new ThroughputResult(target, workload, threadCount, stickiness, window, total, opsPerSecond, perThreadOps);
    }

    /// <summary>
    /// The bound on the post-stop join: once the stop flag is set, a cooperative worker loop
    /// re-reads it and exits within a single iteration, so any worker still running after this
    /// grace period is stuck — fail fast rather than block the harness indefinitely.
    /// </summary>
    private static readonly TimeSpan StopJoinTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Joins every worker after the stop flag has been set, bounding each join so a stuck worker
    /// surfaces a <see cref="TimeoutException"/> instead of hanging the run on an unbounded join.
    /// </summary>
    /// <param name="workers">The worker threads to join.</param>
    private static void JoinAllOrThrow(Thread[] workers)
    {
        foreach (Thread worker in workers)
        {
            if (!worker.Join(StopJoinTimeout))
            {
                throw new TimeoutException(
                    $"Worker '{worker.Name}' did not stop within {StopJoinTimeout.TotalSeconds:0.#}s of the stop signal.");
            }
        }
    }

    /// <summary>Creates the adapter for the requested target over a fresh shared queue instance.</summary>
    /// <param name="target">The queue implementation to drive.</param>
    /// <param name="stickiness">The stickiness factor for the relaxed/strict MultiQueue targets (ignored by the locking baseline).</param>
    private static IThroughputQueue CreateQueue(ThroughputTarget target, int stickiness) => target switch
    {
        ThroughputTarget.MultiQueueRelaxed => new RelaxedQueueAdapter(stickiness),
        ThroughputTarget.MultiQueueDequeueMin => new DequeueMinQueueAdapter(stickiness),
        ThroughputTarget.LockingBaseline => new LockingQueueAdapter(),
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown throughput target."),
    };

    /// <summary>
    /// Runs the worker loop for the given workload until the stop flag flips (or, for
    /// <see cref="ThroughputWorkload.Drain"/>, until the shared queue is drained), returning this
    /// thread's operation count.
    /// </summary>
    private static long RunWorkload(
        ThroughputWorkload workload,
        IThroughputQueue queue,
        int threadIndex,
        int threadCount,
        int drainPerThread,
        Random rng,
        StopFlag stop)
        => workload switch
        {
            ThroughputWorkload.UniformMixed5050 => RunMixed(queue, rng, stop, keyRange: int.MaxValue),
            ThroughputWorkload.NarrowKeyRange => RunMixed(queue, rng, stop, keyRange: NarrowKeyRange),
            ThroughputWorkload.SplitProducerConsumer => RunSplit(queue, threadIndex, threadCount, rng, stop),
            ThroughputWorkload.Drain => RunDrain(queue, stop, drainPerThread),
            _ => throw new ArgumentOutOfRangeException(nameof(workload), workload, "Unknown throughput workload."),
        };

    /// <summary>
    /// The mixed 50/50 loop: each iteration enqueues one element then attempts one dequeue, drawing
    /// priorities from <c>[0, keyRange)</c>. Both the enqueue and the dequeue attempt count as
    /// operations, so the loop reports two ops per iteration. Used by
    /// <see cref="ThroughputWorkload.UniformMixed5050"/> (wide range) and
    /// <see cref="ThroughputWorkload.NarrowKeyRange"/> (1000 distinct values).
    /// </summary>
    private static long RunMixed(IThroughputQueue queue, Random rng, StopFlag stop, int keyRange)
    {
        long ops = 0;
        while (!stop.Stop)
        {
            long priority = keyRange == int.MaxValue ? rng.NextInt64() : rng.Next(keyRange);
            queue.Enqueue(priority);
            ops++;

            queue.TryDequeue();
            ops++;
        }

        return ops;
    }

    /// <summary>
    /// The split producer/consumer loop (the k-LSM killer): a thread is a producer (enqueue-only) or
    /// a consumer (dequeue-only) based on its index parity, so producer and consumer roles are
    /// disjoint per thread. Each enqueue (producer) or dequeue attempt (consumer) counts as one
    /// operation. Producers draw wide-range priorities so the queue does not collapse to a single key.
    /// </summary>
    private static long RunSplit(IThroughputQueue queue, int threadIndex, int threadCount, Random rng, StopFlag stop)
    {
        bool isProducer = (threadIndex & 1) == 0;

        long ops = 0;
        if (isProducer)
        {
            while (!stop.Stop)
            {
                queue.Enqueue(rng.NextInt64());
                ops++;
            }
        }
        else
        {
            while (!stop.Stop)
            {
                queue.TryDequeue();
                ops++;
            }
        }

        return ops;
    }

    /// <summary>
    /// The drain loop: dequeue-only, counting <i>successful</i> dequeues only. Stops when the shared
    /// queue is observed empty (a dequeue fails) or the window ends, whichever comes first — so it can
    /// never hang and reports at most the pre-populated count across all threads.
    /// </summary>
    /// <remarks>
    /// A single failed dequeue is treated as "drained": under the relaxed contract a
    /// <see langword="false"/> means a full verification pass observed every sub-queue empty, so once
    /// any thread sees that, the cooperative drain is finished. This keeps the smoke run fast and the
    /// accounting (successful dequeues ≤ population) exact.
    /// </remarks>
    private static long RunDrain(IThroughputQueue queue, StopFlag stop, int drainPerThread)
    {
        // drainPerThread is unused beyond documenting intent: the queue is shared and already filled
        // before the window, so the loop simply drains the shared instance cooperatively.
        _ = drainPerThread;

        long ops = 0;
        while (!stop.Stop)
        {
            if (!queue.TryDequeue())
            {
                // Observed empty: the cooperative drain is complete for this thread.
                break;
            }

            ops++;
        }

        return ops;
    }

    /// <summary>
    /// A one-field box holding the <c>volatile</c> stop flag so the controller can flip it and every
    /// worker re-reads the same volatile memory location each loop iteration.
    /// </summary>
    private sealed class StopFlag
    {
        /// <summary>When <see langword="true"/>, the window has elapsed and workers must exit.</summary>
        public volatile bool Stop;
    }
}
