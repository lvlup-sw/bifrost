// =============================================================================
// <copyright file="ConservationStressTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================
// Ported from lvlup-sw/DataFerry@2bf0456 (src/DataFerry.Tests/Concurrency/MultiQueue/ConservationStressTests.cs)

using System.Diagnostics;

using Bifrost.Concurrency;

namespace Bifrost.Tests.Concurrency;

/// <summary>
/// Conservation stress tests proving the MultiQueue is a <i>set-conserving</i> structure under
/// many-producer/many-consumer concurrency: every element enqueued is dequeued once, never lost
/// and never duplicated. The relaxed two-choice dequeue reorders elements, so these tests assert
/// on the consumed <i>multiset</i> (sort + sequence-equal), never on order.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a counter, not <c>TryDequeue == false</c>, terminates the consumers.</b> A
/// <see langword="false"/> return only means the queue was observed momentarily empty (the
/// <c>ConcurrentQueue.TryDequeue</c> precedent). While producers are still enqueueing, a transient
/// empty observation is expected and is <i>not</i> a completion signal. Consumers therefore loop
/// until a shared <see cref="Interlocked"/> dequeued-counter reaches the known produced total, which
/// is the only sound termination condition for a still-filling queue.
/// </para>
/// <para>
/// <b>Kill-probe (RED witness).</b> These tests were verified to DETECT a conservation break before
/// being trusted to assert its absence. With <c>SubQueue.TryHeapPop</c> mutated to return the root
/// <i>without removing it</i> (no size decrement, no sift-down: a duplicate factory, the v1-class
/// conservation failure), both tests fail with thousands of duplicate elements detected. With the
/// real heap restored both pass with zero duplicates and zero losses. The mutation procedure is
/// recorded in the task notes; re-run it after any change to <c>TryHeapPop</c> / <c>TryLockedPop</c>.
/// </para>
/// <para>
/// <b>Bounded threads / wall-clock.</b> Each test caps its worker threads at <see cref="MaxThreads"/>
/// (≤ 8) and runs <see cref="NotInParallelAttribute">serially with respect to the other stress
/// tests</see> so concurrent runs on a shared machine cannot oversubscribe the scheduler. A watchdog
/// <see cref="CancellationTokenSource"/> deadline (<see cref="WatchdogTimeout"/>) makes a stuck run
/// fail deterministically instead of hanging. The expected wall-clock is well under a second.
/// </para>
/// </remarks>
[NotInParallel]
public class ConservationStressTests
{
    /// <summary>The hard cap on worker threads per test, so concurrent CI runs cannot oversubscribe.</summary>
    private const int MaxThreads = 8;

    /// <summary>The total number of unique elements produced by test 1's producers.</summary>
    private const int Test1TotalElements = 100_000;

    /// <summary>
    /// The total number of unique elements produced by the churn-near-empty test: ≥10⁶ ops driving
    /// the sparse-fallback routing and the verification scan under the single-item insert/drain storm
    /// that keeps the queue hovering near empty. That is the regime the occupancy bitmask targets and
    /// the one most likely to surface a transition-write or routing staleness defect.
    /// </summary>
    private const int ChurnTotalElements = 1_000_000;

    /// <summary>The per-thread operation count for test 2's mixed-workload threads.</summary>
    private const int Test2OpsPerThread = 25_000;

    /// <summary>The number of mixed-workload threads in test 2 (4 threads × the per-thread op count).</summary>
    private const int Test2ThreadCount = 4;

    /// <summary>
    /// The watchdog deadline. Both tests are expected to finish in well under a second; this bound
    /// exists only so a livelock or lost-progress regression fails the run deterministically instead
    /// of hanging the suite.
    /// </summary>
    private static readonly TimeSpan WatchdogTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Many producers enqueue 100,000 disjoint unique elements while as many consumers drain the
    /// queue concurrently; the union of everything consumed must equal — as a multiset — exactly the
    /// set produced, with the queue empty afterward. This is the headline conservation proof: under
    /// real producer/consumer contention nothing is lost and nothing is duplicated.
    /// </summary>
    [Test]
    public async Task EnqueueDequeue_ManyProducersManyConsumers_EveryElementExactlyOnce()
        => await AssertManyProducersManyConsumersConserve(new ConcurrentPriorityQueue<int, int>()).ConfigureAwait(false);

    /// <summary>
    /// The headline conservation proof re-run with stickiness enabled (<c>s = 4</c>): every thread
    /// reuses a stuck sub-queue selection for several ops, so this exercises the wait-free
    /// reset-on-contention path (<c>ResetStickyEnqueue</c>/<c>ResetStickyDequeue</c>) under real
    /// many-producer/many-consumer contention and confirms stickiness changes only <i>which</i>
    /// sub-queue is sampled, never the exactly-once contract (§5.2). Stale <c>[ThreadStatic]</c>
    /// stuck-state inherited across thread reuse is harmless by construction.
    /// </summary>
    [Test]
    public async Task EnqueueDequeue_ManyProducersManyConsumers_WithStickiness_EveryElementExactlyOnce()
        => await AssertManyProducersManyConsumersConserve(
            new ConcurrentPriorityQueue<int, int>(boundedCapacity: -1, stickiness: 4)).ConfigureAwait(false);

    /// <summary>
    /// The shared body of the headline conservation proof, parameterized only by the supplied
    /// <paramref name="queue"/> so the exact same producer/consumer race and multiset reconciliation
    /// run against both the default (<c>s = 1</c>) and a stickiness-enabled queue. Every element
    /// enqueued must be dequeued exactly once — never lost, never duplicated — with the queue empty
    /// afterward.
    /// </summary>
    /// <param name="queue">The queue under test (default or stickiness-configured).</param>
    private static async Task AssertManyProducersManyConsumersConserve(ConcurrentPriorityQueue<int, int> queue)
    {
        // p producers + p consumers, capped so 2p ≤ MaxThreads (≤ 8) and at least one of each.
        int p = Math.Clamp(Math.Min(4, Environment.ProcessorCount / 2), 1, MaxThreads / 2);

        using var watchdog = new CancellationTokenSource(WatchdogTimeout);

        // Disjoint per-producer ranges so every one of the Test1TotalElements ints is unique. Producer
        // k owns [k*perProducer, (k+1)*perProducer); priority == element so ordering is total.
        int perProducer = Test1TotalElements / p;
        int producedTotal = perProducer * p; // == Test1TotalElements when evenly divisible.

        long dequeuedCount = 0;
        var consumed = new List<int>[p];
        var producers = new Thread[p];
        var consumers = new Thread[p];

        for (int k = 0; k < p; k++)
        {
            int start = k * perProducer;
            int endExclusive = start + perProducer;
            producers[k] = new Thread(() =>
            {
                for (int value = start; value < endExclusive; value++)
                {
                    queue.Enqueue(value, value);
                }
            })
            { IsBackground = true, Name = $"conservation-producer-{k}" };
        }

        for (int k = 0; k < p; k++)
        {
            var localConsumed = new List<int>(producedTotal / p);
            consumed[k] = localConsumed;
            consumers[k] = new Thread(() =>
            {
                // Drain until the shared counter proves every produced element has been removed.
                // A false TryDequeue is NOT a stop signal — producers may still be filling — so we
                // spin (with a tiny yield) until the counter reaches the produced total.
                while (Volatile.Read(ref dequeuedCount) < producedTotal)
                {
                    if (watchdog.IsCancellationRequested)
                    {
                        return;
                    }

                    if (queue.TryDequeue(out int element, out _))
                    {
                        localConsumed.Add(element);
                        Interlocked.Increment(ref dequeuedCount);
                    }
                    else
                    {
                        // Momentarily empty while producers are still running: yield and retry.
                        Thread.Yield();
                    }
                }
            })
            { IsBackground = true, Name = $"conservation-consumer-{k}" };
        }

        // Act — start consumers first so they are already draining as producers fill.
        var sw = Stopwatch.StartNew();
        foreach (var consumer in consumers)
        {
            consumer.Start();
        }

        foreach (var producer in producers)
        {
            producer.Start();
        }

        foreach (var producer in producers)
        {
            producer.Join();
        }

        foreach (var consumer in consumers)
        {
            consumer.Join();
        }

        sw.Stop();

        // The watchdog must not have fired — a fired deadline means the run lost progress.
        await Assert.That(watchdog.IsCancellationRequested).IsFalse().Because(
            $"the producer/consumer run exceeded the {WatchdogTimeout.TotalSeconds:N0}s watchdog " +
            $"(dequeued={Volatile.Read(ref dequeuedCount):N0} of {producedTotal:N0}) — lost progress");

        // Build the expected and actual multisets.
        var expected = new List<int>(producedTotal);
        for (int value = 0; value < producedTotal; value++)
        {
            expected.Add(value);
        }

        var actual = new List<int>(producedTotal);
        foreach (var list in consumed)
        {
            actual.AddRange(list);
        }

        // Cardinality first: exactly the produced count was consumed (no loss, no surplus).
        await Assert.That(actual.Count).IsEqualTo(producedTotal).Because(
            $"every produced element must be consumed exactly once (run took {sw.ElapsedMilliseconds:N0} ms)");

        // No duplicates: distinct count equals total count.
        int distinctCount = new HashSet<int>(actual).Count;
        await Assert.That(distinctCount).IsEqualTo(producedTotal).Because(
            $"a duplicated element was dequeued — conservation broken ({actual.Count - distinctCount:N0} duplicates)");

        // Multiset equality: sorted consumed == sorted produced (proves no loss AND no duplication).
        actual.Sort();
        await Assert.That(actual.SequenceEqual(expected)).IsTrue().Because(
            "the consumed multiset must equal the produced set exactly (order is irrelevant)");

        // The queue is drained.
        await Assert.That(queue.Count).IsEqualTo(0);
        await Assert.That(queue.IsEmpty).IsTrue();
    }

    /// <summary>
    /// A random 50/50 enqueue/dequeue mix across four threads, each with its own seeded RNG and its
    /// own thread-id-tagged unique value space. After joining, the queue is drained single-threaded
    /// and the books are reconciled: everything enqueued by anyone equals everything dequeued by
    /// anyone plus everything drained, with no value appearing twice anywhere. This exercises the
    /// interleaved-operation path the headline test does not — pops racing pushes on the same stripes.
    /// </summary>
    [Test]
    public async Task MixedWorkload_RandomOpMix_NoLostOrDuplicatedElements()
    {
        var queue = new ConcurrentPriorityQueue<long, long>();
        using var watchdog = new CancellationTokenSource(WatchdogTimeout);

        var enqueuedByThread = new List<long>[Test2ThreadCount];
        var dequeuedByThread = new List<long>[Test2ThreadCount];
        var threads = new Thread[Test2ThreadCount];

        for (int t = 0; t < Test2ThreadCount; t++)
        {
            int threadId = t;
            var enqueued = new List<long>(Test2OpsPerThread);
            var dequeued = new List<long>(Test2OpsPerThread);
            enqueuedByThread[t] = enqueued;
            dequeuedByThread[t] = dequeued;

            threads[t] = new Thread(() =>
            {
                // Seeded per thread for reproducibility. Values are tagged with the thread id in the
                // high bits and a monotonically increasing local counter in the low bits, so every
                // value any thread enqueues is globally unique — a duplicate anywhere is a true bug.
                var rng = new Random(1000 + threadId);
                long tag = (long)threadId << 40;
                long localCounter = 0;

                for (int op = 0; op < Test2OpsPerThread; op++)
                {
                    if (watchdog.IsCancellationRequested)
                    {
                        return;
                    }

                    if (rng.Next(2) == 0)
                    {
                        long value = tag | localCounter++;
                        queue.Enqueue(value, value);
                        enqueued.Add(value);
                    }
                    else if (queue.TryDequeue(out long element, out _))
                    {
                        dequeued.Add(element);
                    }

                    // A false dequeue is fine here — the op simply did nothing; the books still balance
                    // because we only record values that were actually enqueued or actually removed.
                }
            })
            { IsBackground = true, Name = $"mixed-workload-{t}" };
        }

        // Act.
        var sw = Stopwatch.StartNew();
        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        // Drain whatever remains, single-threaded, to complete the books. The drain is capped at a
        // safety ceiling: a healthy queue cannot yield more than was ever enqueued, so if the drain
        // exceeds the total-enqueued count the queue is already over-producing (a duplicate-factory
        // conservation break) — stop draining and let the reconciliation assertions report it cleanly
        // instead of growing the list until OutOfMemory. The ceiling is the kill-probe's fast, clean
        // failure path; on a conserving queue it is never reached.
        int totalEnqueued = 0;
        foreach (var list in enqueuedByThread)
        {
            totalEnqueued += list.Count;
        }

        int drainCeiling = totalEnqueued + 1;
        var drained = new List<long>();
        while (drained.Count <= drainCeiling && queue.TryDequeue(out long element, out _))
        {
            drained.Add(element);
        }

        sw.Stop();

        await Assert.That(watchdog.IsCancellationRequested).IsFalse().Because(
            $"the mixed workload exceeded the {WatchdogTimeout.TotalSeconds:N0}s watchdog — lost progress");

        // Reconcile. Everything enqueued by anyone...
        var allEnqueued = new List<long>();
        foreach (var list in enqueuedByThread)
        {
            allEnqueued.AddRange(list);
        }

        // ...must equal everything dequeued-by-anyone PLUS everything drained at the end.
        var allRemoved = new List<long>(allEnqueued.Count);
        foreach (var list in dequeuedByThread)
        {
            allRemoved.AddRange(list);
        }

        allRemoved.AddRange(drained);

        // Cardinality: as many elements left the queue as entered it (no loss, no surplus).
        await Assert.That(allRemoved.Count).IsEqualTo(allEnqueued.Count).Because(
            $"every enqueued element must be removed exactly once across run+drain " +
            $"(run took {sw.ElapsedMilliseconds:N0} ms; enqueued={allEnqueued.Count:N0}, removed={allRemoved.Count:N0})");

        // No duplicates anywhere: values are globally unique, so a HashSet over all removed values
        // must have the same cardinality as the list — any collision is a duplicated dequeue.
        int distinctRemoved = new HashSet<long>(allRemoved).Count;
        await Assert.That(distinctRemoved).IsEqualTo(allRemoved.Count).Because(
            $"a value was dequeued more than once — conservation broken ({allRemoved.Count - distinctRemoved:N0} duplicates)");

        // Multiset equality (which, given uniqueness, is set equality): enqueued == removed exactly.
        allEnqueued.Sort();
        allRemoved.Sort();
        await Assert.That(allRemoved.SequenceEqual(allEnqueued)).IsTrue().Because(
            "the removed multiset must equal the enqueued multiset exactly — no element lost or duplicated");

        // The queue is fully drained.
        await Assert.That(queue.IsEmpty).IsTrue();
    }

    /// <summary>
    /// Returns <see langword="true"/> only when the queue's own <c>Count</c> stays above
    /// <paramref name="inFlightTolerance"/> across a short recheck spin, evidence of a genuinely
    /// resident element. Count can transiently over-report by up to one per consumer mid-pop: each
    /// publishes its sub-queue's seqlock empty flag before writing the striped <c>Count = 0</c> (two
    /// ordered writes under the held lock), so a Count at or below the number of concurrent consumers
    /// could be entirely in-flight removals, however long a preempted consumer stalls. Keying the
    /// threshold to the consumer count makes the witness sound regardless of scheduling, where a
    /// fixed-iteration wait could not. Used only as a stress-test witness with production quiescent.
    /// </summary>
    /// <param name="queue">The queue to probe.</param>
    /// <param name="inFlightTolerance">Maximum Count attributable to concurrent in-flight removals.</param>
    /// <returns><see langword="true"/> if <c>Count</c> stays above the tolerance across the spin.</returns>
    private static bool PersistentlyNonEmpty(ConcurrentPriorityQueue<int, int> queue, int inFlightTolerance)
    {
        // Cheap exit: a Count within the in-flight tolerance proves nothing resident.
        if (queue.Count <= inFlightTolerance)
        {
            return false;
        }

        // Re-confirm across a bounded spin; a single drop to within tolerance means the apparent
        // residents were in-flight removals whose Count writes have now landed, not a violation.
        var spinner = new SpinWait();
        for (int i = 0; i < 64; i++)
        {
            if (queue.Count <= inFlightTolerance)
            {
                return false;
            }

            spinner.SpinOnce();
        }

        return true;
    }

    /// <summary>
    /// Churn-near-empty conservation and no-false-empty proof: many producers each insert
    /// <i>single</i> items as fast as consumers drain them, so the queue continuously hovers near
    /// empty. That regime exercises the occupancy bitmask's transition writes and the sparse-fallback
    /// routing hardest, and is where a lost-clear or stale-set staleness bug would surface. Over ≥10⁶
    /// ops the consumed multiset must equal the produced set exactly (nothing lost or duplicated), and
    /// the staleness-specific assertion is that no <c>TryDequeue</c> may return <see langword="false"/>
    /// while elements demonstrably remain after all production completes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Run in Release (the project lesson: Debug-green ≠ Release-green for concurrency timing). Two
    /// independent witnesses guard correctness:
    /// </para>
    /// <list type="bullet">
    /// <item>the conservation multiset (every unique value produced is consumed exactly once), the
    /// authority for "no element lost via a stale/lost-clear routing miss"; and</item>
    /// <item>a quiescent-tail no-false-empty check: after producers finish, any remaining elements
    /// must be drainable. A <see langword="false"/> while the queue's own <c>Count</c> is still
    /// positive and no producer is in flight is an unambiguous staleness violation: a stale-clear
    /// that fooled both routing and the scan, which the occupancy design must make impossible.</item>
    /// </list>
    /// </remarks>
    [Test]
    public async Task ChurnNearEmpty_SingleItemInsertDrainStorm_NoFalseEmptyNoLostElement()
    {
        var queue = new ConcurrentPriorityQueue<int, int>();

        // p producers + p consumers, capped so 2p ≤ MaxThreads, at least one of each.
        int p = Math.Clamp(Math.Min(4, Environment.ProcessorCount / 2), 1, MaxThreads / 2);
        using var watchdog = new CancellationTokenSource(WatchdogTimeout);

        int perProducer = ChurnTotalElements / p;
        int producedTotal = perProducer * p;

        // Keep the queue in the near-empty regime this test targets: producers hold back whenever the
        // outstanding (enqueued-but-not-yet-dequeued) backlog rises above this small bound, so a fast
        // producer / slow consumer schedule can't quietly turn it into a dense test. Small enough to
        // stay well inside the sparse regime, large enough not to over-serialize the churn.
        int nearEmptyBacklogCap = p * 8;

        long enqueuedTotal = 0;
        long dequeuedCount = 0;
        long producersDone = 0;
        long falseWhileResident = 0;
        var consumed = new List<int>[p];
        var producers = new Thread[p];
        var consumers = new Thread[p];

        for (int k = 0; k < p; k++)
        {
            int start = k * perProducer;
            int endExclusive = start + perProducer;
            producers[k] = new Thread(() =>
            {
                // Single-item inserts with backpressure: push one only once the outstanding backlog is
                // back under the cap, so consumers stay caught up and the queue keeps hovering near
                // empty. Disjoint per-producer ranges keep every value globally unique, so a duplicate
                // anywhere is a true conservation break.
                var backoff = new SpinWait();
                for (int value = start; value < endExclusive; value++)
                {
                    while (Volatile.Read(ref enqueuedTotal) - Volatile.Read(ref dequeuedCount) > nearEmptyBacklogCap)
                    {
                        if (watchdog.IsCancellationRequested)
                        {
                            return;
                        }

                        backoff.SpinOnce();
                    }

                    if (watchdog.IsCancellationRequested)
                    {
                        return;
                    }

                    queue.Enqueue(value, value);
                    Interlocked.Increment(ref enqueuedTotal);
                }

                Interlocked.Increment(ref producersDone);
            })
            { IsBackground = true, Name = $"churn-producer-{k}" };
        }

        for (int k = 0; k < p; k++)
        {
            var localConsumed = new List<int>(perProducer);
            consumed[k] = localConsumed;
            consumers[k] = new Thread(() =>
            {
                while (Volatile.Read(ref dequeuedCount) < producedTotal)
                {
                    if (watchdog.IsCancellationRequested)
                    {
                        return;
                    }

                    if (queue.TryDequeue(out int element, out _))
                    {
                        localConsumed.Add(element);
                        Interlocked.Increment(ref dequeuedCount);
                        continue;
                    }

                    // A false was returned. Only once every producer has finished is the queue
                    // quiescent enough that a false-while-resident is unambiguous: with no in-flight
                    // enqueue to excuse a stale observation, a false while a poppable element
                    // demonstrably remains means routing and the scan both concluded emptiness while
                    // an element was resident, the staleness violation the occupancy design must make
                    // impossible.
                    //
                    // The witness must be sound against a benign transient. A concurrent consumer
                    // mid-pop publishes its sub-queue's seqlock EmptyFlag = empty before it writes the
                    // striped Count = 0 (two ordered writes under the held lock), so the scan's
                    // lock-free cheap route can legitimately observe "empty" while queue.Count still
                    // counts that being-removed element. Up to p consumers can be mid-pop at once, so
                    // Count over-reports by at most p; only a Count that stays ABOVE p proves a
                    // genuinely resident element, and that bound holds however long a preempted
                    // consumer stalls (a fixed-iteration wait would not).
                    if (Volatile.Read(ref producersDone) == p && PersistentlyNonEmpty(queue, inFlightTolerance: p))
                    {
                        Interlocked.Increment(ref falseWhileResident);
                    }

                    Thread.Yield();
                }
            })
            { IsBackground = true, Name = $"churn-consumer-{k}" };
        }

        // Act — start consumers first so they are draining the instant producers begin (maximizing
        // near-empty churn), then start producers.
        var sw = Stopwatch.StartNew();
        foreach (var consumer in consumers)
        {
            consumer.Start();
        }

        foreach (var producer in producers)
        {
            producer.Start();
        }

        foreach (var producer in producers)
        {
            producer.Join();
        }

        foreach (var consumer in consumers)
        {
            consumer.Join();
        }

        sw.Stop();

        await Assert.That(watchdog.IsCancellationRequested).IsFalse().Because(
            $"the churn run exceeded the {WatchdogTimeout.TotalSeconds:N0}s watchdog " +
            $"(dequeued={Volatile.Read(ref dequeuedCount):N0} of {producedTotal:N0}) — lost progress");

        // The staleness-specific witness: never a false while elements were provably resident and
        // production quiescent.
        await Assert.That(Volatile.Read(ref falseWhileResident)).IsEqualTo(0L).Because(
            "no TryDequeue may return false while elements demonstrably remained and no producer was " +
            "in flight: a lost-clear that fooled both routing and the scan, which cannot happen here");

        // Conservation: build the produced/consumed multisets and reconcile.
        var actual = new List<int>(producedTotal);
        foreach (var list in consumed)
        {
            actual.AddRange(list);
        }

        await Assert.That(actual.Count).IsEqualTo(producedTotal).Because(
            $"every produced element must be consumed exactly once (run took {sw.ElapsedMilliseconds:N0} ms)");

        int distinctCount = new HashSet<int>(actual).Count;
        await Assert.That(distinctCount).IsEqualTo(producedTotal).Because(
            $"a duplicated element was dequeued — conservation broken ({actual.Count - distinctCount:N0} duplicates)");

        var expected = new List<int>(producedTotal);
        for (int value = 0; value < producedTotal; value++)
        {
            expected.Add(value);
        }

        actual.Sort();
        await Assert.That(actual.SequenceEqual(expected)).IsTrue().Because(
            "the consumed multiset must equal the produced set exactly — no element lost or duplicated under churn");

        await Assert.That(queue.Count).IsEqualTo(0);
        await Assert.That(queue.IsEmpty).IsTrue();
    }
}
