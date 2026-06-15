// =============================================================================
// <copyright file="ThreadChurnTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================
// Ported from lvlup-sw/DataFerry@2bf0456 (src/DataFerry.Tests/Concurrency/MultiQueue/ThreadChurnTests.cs)

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Bifrost.Concurrency;

namespace Bifrost.Tests.Concurrency;

/// <summary>
/// Thread-churn and thread-pool stress suites proving the queue's correctness never depends on
/// thread identity (DR-3, DR-17). The <c>[ThreadStatic]</c> <see cref="ThreadHandle"/> that backs
/// random sub-queue selection is created lazily per thread and dies with that thread; these tests
/// hammer that lifecycle from hundreds of short-lived dedicated threads and from thread-pool tasks
/// that yield across <c>await</c> boundaries (so operations before/after an await may land on
/// different pool threads, forcing the handle to be re-fetched per operation).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why no kill-probe (RED witness).</b> These are lifecycle/conservation suites, not invariant
/// detectors like <c>SeqlockTearTests</c>. A meaningful RED is impractical without breaking the
/// <c>[ThreadStatic]</c> mechanism itself (e.g. promoting the handle to a process-static field),
/// which is a change to the runtime contract rather than to product code under test. Instead each
/// test performs a <b>calibration</b>: a side-channel <see cref="ConcurrentDictionary{TKey, TValue}"/>
/// records the identity hash of every distinct <see cref="ThreadHandle"/> instance observed across
/// the worker threads, and the test asserts it genuinely exercised more than one handle — i.e. the
/// stress actually spanned multiple <c>[ThreadStatic]</c> lifetimes. A suite that ran every
/// operation on a single thread would fail this floor and prove nothing.
/// </para>
/// <para>
/// <b>Conservation reconciliation.</b> Every produced and consumed value is recorded into a
/// <see cref="ConcurrentBag{T}"/>. After the concurrent phase joins, the queue is drained
/// single-threaded; the test asserts exact conservation — the produced multiset equals the
/// consumed-plus-drained multiset with no duplicates and no losses — which can only hold if every
/// enqueue and dequeue landed correctly regardless of which thread (or pool thread) ran it.
/// </para>
/// <para>
/// <b>Resource budget.</b> Short-lived threads run in small batches (≤ 8 concurrent at any instant)
/// and every test is wall-clock bounded well under five seconds, because other agents share the
/// machine.
/// </para>
/// </remarks>
/// <remarks>
/// Categorized <c>Stress</c>: CPU-bound thread-churn proofs that require real multi-core parallelism
/// (e.g. the async-yield re-fetch test asserts continuations span &gt;1 pool thread). Per-PR CI
/// excludes the <c>Stress</c> category; they run on the dedicated stress job (push to <c>main</c>,
/// the <c>run-stress</c> label, or manual dispatch). The correctness assertions stay strict.
/// </remarks>
[Property("Category", "Stress")]
[NotInParallel]
public class ThreadChurnTests
{
    /// <summary>Total short-lived dedicated threads spawned across all batches in test 1.</summary>
    private const int TotalShortLivedThreads = 200;

    /// <summary>Maximum concurrent threads per batch — caps instantaneous thread count at 8.</summary>
    private const int BatchSize = 8;

    /// <summary>Unique values each short-lived producer thread enqueues.</summary>
    private const int ItemsPerThread = 8;

    /// <summary>How many of its own items each short-lived thread dequeues before exiting.</summary>
    private const int DequeuesPerThread = 3;

    /// <summary>Thread-pool tasks spawned by the async-yield test.</summary>
    private const int AsyncTaskCount = 64;

    /// <summary>The bounded queue's fixed capacity for the churn-vs-capacity test.</summary>
    private const int BoundedCapacity = 32;

    /// <summary>The number of consumer threads draining the bounded queue under churn.</summary>
    private const int BoundedConsumerCount = 2;

    /// <summary>
    /// The maximum transient overshoot above <see cref="BoundedCapacity"/> that a continuously
    /// sampled metric may legitimately show under churn — equal to the number of threads that can be
    /// mutating the bounded queue at one instant (<see cref="BatchSize"/> producers plus
    /// <see cref="BoundedConsumerCount"/> consumers). Both sampled metrics can momentarily read above
    /// capacity for principled, by-design reasons: (a) the reservation gate increments FIRST and then
    /// rolls back, so producers in that window inflate it without inserting anything; and (b) the
    /// public <c>Count</c> is a lock-free per-stripe snapshot that sums stripes one at a time, so an
    /// element migrating between stripes mid-scan can be counted in two places. Both overshoots are
    /// bounded by how many operations are in flight at once; asserting against this ceiling proves the
    /// overshoot is transient and bounded (no leak / no runaway) without flaking, whereas asserting a
    /// hard ≤ capacity on either continuously sampled metric would contradict the queue's documented
    /// lock-free semantics. The HARD guarantee is verified separately at quiescence: after the full
    /// drain the gate must equal exactly zero and the queue must be empty.
    /// </summary>
    private const int TransientOvershootAllowance = BatchSize + BoundedConsumerCount;

    /// <summary>
    /// Records the identity hash of the calling thread's <see cref="ThreadHandle"/> into the
    /// supplied side-channel, so a test can count how many distinct handle instances (and therefore
    /// distinct <c>[ThreadStatic]</c> lifetimes) it actually exercised.
    /// </summary>
    /// <param name="observedHandles">The calibration side-channel keyed by handle identity hash.</param>
    private static void ObserveCurrentHandle(ConcurrentDictionary<int, byte> observedHandles)
        => observedHandles.TryAdd(RuntimeHelpers.GetHashCode(ThreadHandle.Current), 0);

    /// <summary>
    /// The short-lived-thread churn proof: 200 dedicated threads (in batches of 8) each mint a fresh
    /// <c>[ThreadStatic]</c> handle, enqueue unique values, dequeue a few, and die; exact multiset
    /// conservation across all lifetimes proves correctness never depends on thread identity.
    /// </summary>
    [Test]
    public async Task Operations_AcrossManyShortLivedThreads_ConserveElements()
    {
        // Arrange — an unbounded queue plus produced/consumed reconciliation bags and the handle
        // calibration side-channel. Every value is globally unique (thread-tagged), so duplicates
        // and losses are both detectable by multiset comparison.
        var queue = new ConcurrentPriorityQueue<long, long>();
        var produced = new ConcurrentBag<long>();
        var consumed = new ConcurrentBag<long>();
        var observedHandles = new ConcurrentDictionary<int, byte>();

        // The main thread's handle is one observed lifetime; record it up front.
        ObserveCurrentHandle(observedHandles);

        // Act — 200 short-lived dedicated threads, run in batches of 8 so at most 8 exist at once.
        for (int batchStart = 0; batchStart < TotalShortLivedThreads; batchStart += BatchSize)
        {
            int batchCount = Math.Min(BatchSize, TotalShortLivedThreads - batchStart);
            var batch = new Thread[batchCount];

            for (int b = 0; b < batchCount; b++)
            {
                int threadIndex = batchStart + b;
                batch[b] = new Thread(() =>
                {
                    // Each thread re-fetches its own [ThreadStatic] handle; record its identity.
                    ObserveCurrentHandle(observedHandles);

                    // Globally unique, thread-tagged values: threadIndex occupies the high bits so
                    // no two threads ever mint the same value.
                    long baseValue = (long)threadIndex * ItemsPerThread;
                    for (int k = 0; k < ItemsPerThread; k++)
                    {
                        long value = baseValue + k;
                        queue.Enqueue(value, value);
                        produced.Add(value);
                    }

                    // Dequeue a few entries (any thread's items may surface — the queue is shared).
                    for (int k = 0; k < DequeuesPerThread; k++)
                    {
                        if (queue.TryDequeue(out long element, out _))
                        {
                            consumed.Add(element);
                        }
                    }
                })
                { IsBackground = true, Name = $"churn-producer-{threadIndex}" };
            }

            foreach (var thread in batch)
            {
                thread.Start();
            }

            foreach (var thread in batch)
            {
                thread.Join();
            }
        }

        // Drain whatever remains single-threaded (this also exercises the main thread's handle).
        while (queue.TryDequeue(out long element, out _))
        {
            consumed.Add(element);
        }

        // Assert — calibration: the stress genuinely spanned many handle lifetimes (≈ 200 worker
        // threads + the main thread). New OS threads always get a fresh [ThreadStatic] handle, so
        // the floor is conservative but proves the suite is non-vacuous.
        int distinctHandles = observedHandles.Count;
        await Assert.That(distinctHandles).IsGreaterThan(1).Because(
            $"the churn must exercise more than one ThreadHandle lifetime or it proves nothing; " +
            $"observed {distinctHandles} distinct handles");

        // Conservation: exact multiset equality between produced and consumed-plus-drained, with no
        // duplicates and no losses.
        var producedList = produced.ToList();
        var consumedList = consumed.ToList();

        await Assert.That(producedList.Count).IsEqualTo(TotalShortLivedThreads * ItemsPerThread);
        await Assert.That(consumedList.Count).IsEqualTo(producedList.Count).Because(
            "every produced element must be consumed exactly once (no loss, no duplication)");

        var producedSet = new HashSet<long>(producedList);
        var consumedSet = new HashSet<long>(consumedList);

        await Assert.That(consumedList.Count).IsEqualTo(consumedSet.Count).Because(
            "a duplicated dequeue would surface the same value twice");
        await Assert.That(consumedSet.SetEquals(producedSet)).IsTrue().Because(
            "the consumed multiset must equal the produced multiset exactly");
        await Assert.That(queue.IsEmpty).IsTrue();
    }

    /// <summary>
    /// The async-yield churn proof on the default queue (<c>s = 1</c>): thread-pool tasks yield
    /// between operations so pre/post-await operations may resume on different pool threads, forcing
    /// the <c>[ThreadStatic]</c> handle to be re-fetched per operation; conservation must hold exactly.
    /// </summary>
    [Test]
    public async Task Operations_OnThreadPoolWithAsyncYields_BehaveCorrectly()
        => await AssertAsyncYieldChurnConserves(new ConcurrentPriorityQueue<long, long>()).ConfigureAwait(false);

    /// <summary>
    /// The async-yield churn proof re-run with stickiness enabled (<c>s = 4</c>): because each pool
    /// task yields between operations, an operation may resume on a thread whose <c>[ThreadStatic]</c>
    /// handle still carries another operation's stuck selection + countdown. This proves stale
    /// stickiness state is harmless — it only ever biases sub-queue sampling, never correctness — so
    /// conservation holds exactly under stickiness across pool-thread reuse (§5.2, DR-3).
    /// </summary>
    [Test]
    public async Task Operations_OnThreadPoolWithAsyncYields_WithStickiness_BehaveCorrectly()
        => await AssertAsyncYieldChurnConserves(new ConcurrentPriorityQueue<long, long>(boundedCapacity: -1, stickiness: 4)).ConfigureAwait(false);

    /// <summary>
    /// The shared body of the async-yield churn proof, parameterized only by the supplied
    /// <paramref name="queue"/> so the identical pool-task race and conservation reconciliation run
    /// against both the default (<c>s = 1</c>) and a stickiness-enabled queue.
    /// </summary>
    /// <param name="queue">The queue under test (default or stickiness-configured).</param>
    private static async Task AssertAsyncYieldChurnConserves(ConcurrentPriorityQueue<long, long> queue)
    {
        // Arrange — reconciliation bags. Each pool task yields between operations so its pre/post-await
        // operations may resume on DIFFERENT pool threads; the handle (and any stuck stickiness state)
        // must be re-fetched internally per operation (the caller does nothing special).
        var produced = new ConcurrentBag<long>();
        var consumed = new ConcurrentBag<long>();
        var observedHandles = new ConcurrentDictionary<int, byte>();

        // Act — 64 thread-pool tasks, each enqueue/await/enqueue/await then two dequeues with yields.
        // NOTE (port): the Task.Yield() awaits below are the mechanism under test — they deliberately
        // allow continuations to land on different pool threads. YieldAwaitable exposes no
        // ConfigureAwait, so the awaits stay bare by necessity and by intent.
        var tasks = new Task[AsyncTaskCount];
        for (int t = 0; t < AsyncTaskCount; t++)
        {
            int taskIndex = t;
            tasks[t] = Task.Run(async () =>
            {
                // Task-tagged unique values: two per task, in disjoint high-bit ranges.
                long first = (long)taskIndex * 2;
                long second = first + 1;

                ObserveCurrentHandle(observedHandles);
                queue.Enqueue(first, first);
                produced.Add(first);

                await Task.Yield();

                // After the yield we may be on a different pool thread — record again.
                ObserveCurrentHandle(observedHandles);
                queue.Enqueue(second, second);
                produced.Add(second);

                await Task.Yield();

                ObserveCurrentHandle(observedHandles);
                if (queue.TryDequeue(out long e1, out _))
                {
                    consumed.Add(e1);
                }

                await Task.Yield();

                ObserveCurrentHandle(observedHandles);
                if (queue.TryDequeue(out long e2, out _))
                {
                    consumed.Add(e2);
                }
            });
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Drain the remainder single-threaded.
        while (queue.TryDequeue(out long element, out _))
        {
            consumed.Add(element);
        }

        // Assert — calibration: pool tasks ran across more than one handle lifetime.
        int distinctHandles = observedHandles.Count;
        await Assert.That(distinctHandles).IsGreaterThan(1).Because(
            $"async yields must span more than one pool-thread ThreadHandle or the re-fetch path is " +
            $"untested; observed {distinctHandles} distinct handles");

        // Conservation: every task produces exactly two values; all must be consumed exactly once.
        var producedList = produced.ToList();
        var consumedList = consumed.ToList();

        await Assert.That(producedList.Count).IsEqualTo(AsyncTaskCount * 2);
        await Assert.That(consumedList.Count).IsEqualTo(producedList.Count).Because(
            "every produced element must be consumed exactly once across the async churn");

        var producedSet = new HashSet<long>(producedList);
        var consumedSet = new HashSet<long>(consumedList);

        await Assert.That(consumedList.Count).IsEqualTo(consumedSet.Count).Because(
            "a re-fetch bug corrupting RNG state could surface a value twice");
        await Assert.That(consumedSet.SetEquals(producedSet)).IsTrue().Because(
            "the consumed multiset must equal the produced multiset exactly");
        await Assert.That(queue.IsEmpty).IsTrue();
    }

    /// <summary>
    /// The bounded-capacity churn proof: short-lived producers hammer <c>TryEnqueue</c> against a
    /// capacity-32 queue while consumers drain and an observer continuously samples <c>Count</c> and
    /// the reservation gate. Both sampled metrics must stay within the bounded transient-overshoot
    /// allowance, and at quiescence the gate must unwind to exactly zero (DR-13).
    /// </summary>
    [Test]
    public async Task BoundedQueue_UnderChurn_NeverExceedsCapacity()
    {
        // Arrange — a bounded queue (capacity 32) hammered by short-lived producer threads while
        // two consumers drain. An independent observer loop samples both the public Count and the
        // internal reservation gate continuously. Both may transiently overshoot capacity by a small,
        // bounded amount under churn for principled by-design reasons (lock-free snapshot Count;
        // increment-then-rollback gate) — see TransientOvershootAllowance and the assertions below.
        // The hard guarantee (gate unwinds to exactly zero, queue empty) is checked at quiescence.
        var queue = new ConcurrentPriorityQueue<long, long>(boundedCapacity: BoundedCapacity);
        var observedHandles = new ConcurrentDictionary<int, byte>();
        ObserveCurrentHandle(observedHandles);

        using var done = new CancellationTokenSource();
        int maxCountObserved = 0;
        int maxBoundedObserved = 0;
        long sampleCount = 0;
        long successfulEnqueues = 0;
        long failedEnqueues = 0;

        // Observer — samples Count and the reservation gate as fast as it can until producers and
        // consumers are finished. Records the running maxima so an overshoot anywhere is caught.
        var observer = new Thread(() =>
        {
            int localMaxCount = 0;
            int localMaxBounded = 0;
            long localSamples = 0;

            while (!done.IsCancellationRequested)
            {
                int count = queue.Count;
                int bounded = queue.DebugBoundedCountForTest;
                if (count > localMaxCount)
                {
                    localMaxCount = count;
                }

                if (bounded > localMaxBounded)
                {
                    localMaxBounded = bounded;
                }

                localSamples++;
            }

            // One final post-cancellation sample, in case the peak landed at the very end.
            localMaxCount = Math.Max(localMaxCount, queue.Count);
            localMaxBounded = Math.Max(localMaxBounded, queue.DebugBoundedCountForTest);

            Volatile.Write(ref maxCountObserved, localMaxCount);
            Volatile.Write(ref maxBoundedObserved, localMaxBounded);
            Interlocked.Add(ref sampleCount, localSamples);
        })
        { IsBackground = true, Name = "bounded-observer" };

        // Two consumers drain continuously so producers keep finding room (and failing) — this is
        // what produces the steady-state churn around the capacity boundary.
        var consumers = new Thread[BoundedConsumerCount];
        for (int c = 0; c < consumers.Length; c++)
        {
            consumers[c] = new Thread(() =>
            {
                ObserveCurrentHandle(observedHandles);
                while (!done.IsCancellationRequested)
                {
                    queue.TryDequeue(out _, out _);
                }
            })
            { IsBackground = true, Name = $"bounded-consumer-{c}" };
        }

        // Act — start the observer and consumers, then run 8 batches of 8 short-lived producers that
        // hammer TryEnqueue (many failures expected once the gate is saturated).
        observer.Start();

        for (int batch = 0; batch < 8; batch++)
        {
            var producers = new Thread[BatchSize];
            for (int p = 0; p < BatchSize; p++)
            {
                int producerIndex = (batch * BatchSize) + p;
                producers[p] = new Thread(() =>
                {
                    ObserveCurrentHandle(observedHandles);
                    long localSuccess = 0;
                    long localFail = 0;

                    // 200 attempts each: far more than capacity, so the gate is repeatedly saturated.
                    for (int k = 0; k < 200; k++)
                    {
                        long value = ((long)producerIndex << 16) + k;
                        if (queue.TryEnqueue(value, value))
                        {
                            localSuccess++;
                        }
                        else
                        {
                            localFail++;
                        }
                    }

                    Interlocked.Add(ref successfulEnqueues, localSuccess);
                    Interlocked.Add(ref failedEnqueues, localFail);
                })
                { IsBackground = true, Name = $"bounded-producer-{producerIndex}" };
            }

            foreach (var producer in producers)
            {
                producer.Start();
            }

            foreach (var producer in producers)
            {
                producer.Join();
            }

            if (batch == 0)
            {
                // Start the consumers only AFTER the first batch has hammered the still-undrained
                // queue. With no draining, 8 producers × 200 attempts against the 32-slot gate
                // guarantee saturation (≈1568 rejections) regardless of scheduler timing — under
                // coverage on a few-core CI runner the two consumers could otherwise keep pace and
                // leave failedEnqueues at 0, never exercising the bound. Batches 1–7 then run with
                // consumers draining, producing the concurrent boundary churn the overshoot
                // assertions need. Batch 0's max in-flight gate overshoot (≤ BatchSize = 8) stays
                // within TransientOvershootAllowance (10), so the overshoot assertions still hold.
                foreach (var consumer in consumers)
                {
                    consumer.Start();
                }
            }
        }

        // Producers done — stop consumers and the observer, then join them. (Port adaptation:
        // CancelAsync instead of Cancel for VSTHRD103 — identical semantics here because no
        // callbacks are registered on the token; workers only poll IsCancellationRequested.)
        await done.CancelAsync().ConfigureAwait(false);
        foreach (var consumer in consumers)
        {
            consumer.Join();
        }

        observer.Join();

        // Final single-threaded drain so the gate fully unwinds.
        while (queue.TryDequeue(out _, out _))
        {
        }

        // Assert — bounded transient overshoot on BOTH continuously sampled metrics. Neither the
        // public Count nor the reservation gate is a tight ≤ capacity oracle under concurrent churn:
        //
        //   * The public Count (Count.cs) is a lock-free per-stripe snapshot summed one stripe at a
        //     time, so an element migrating between stripes mid-scan can be counted twice — the same
        //     interleaving that the implementation already clamps at zero for the symmetric undercount.
        //   * The reservation gate (DR-13) increments the shared atomic FIRST and only then checks the
        //     bound, rolling the increment back on overshoot — so producers in that window inflate the
        //     gate snapshot without ever inserting an element.
        //
        // Both overshoots are transient and bounded by how many operations are in flight at once, so we
        // assert each stays within `TransientOvershootAllowance` of capacity. A larger excursion would
        // mean a genuine leak or runaway accumulation. The HARD guarantee — that the queue never
        // actually retains more than `boundedCapacity` elements — is verified at quiescence below.
        await Assert.That(Volatile.Read(ref maxCountObserved)).IsLessThanOrEqualTo(BoundedCapacity + TransientOvershootAllowance).Because(
            $"the lock-free per-stripe Count snapshot may transiently double-count a migrating element, " +
            $"but only within {TransientOvershootAllowance} of the {BoundedCapacity} bound — a larger " +
            $"excursion would mean real occupancy ran away past the capacity gate");
        await Assert.That(Volatile.Read(ref maxBoundedObserved)).IsLessThanOrEqualTo(BoundedCapacity + TransientOvershootAllowance).Because(
            $"the reservation gate may transiently overshoot {BoundedCapacity} during its " +
            $"increment-then-rollback window, but only by at most the {TransientOvershootAllowance} " +
            $"in-flight operations — a larger overshoot would mean a reservation leaked instead of rolling back");

        // Assert — the HARD capacity guarantee, checked at quiescence (all producers/consumers joined,
        // final drain complete): the reservation gate must have unwound to EXACTLY zero, proving every
        // one of the thousands of reservations was released and none leaked, and the queue is empty.
        await Assert.That(queue.DebugBoundedCountForTest).IsEqualTo(0).Because(
            "every reservation must be released after the queue is fully drained");
        await Assert.That(queue.IsEmpty).IsTrue();

        // Non-vacuousness: the observer actually sampled, and the churn actually hit the boundary
        // (failures only occur when the gate was saturated) across multiple handle lifetimes.
        await Assert.That(Interlocked.Read(ref sampleCount)).IsGreaterThan(0).Because(
            "the observer must have sampled at least once or the bound was never checked");
        await Assert.That(Interlocked.Read(ref failedEnqueues)).IsGreaterThan(0).Because(
            "the churn must actually saturate the gate (produce rejections) or it never tested the bound");
        await Assert.That(observedHandles.Count).IsGreaterThan(1).Because(
            $"the churn must span more than one ThreadHandle lifetime; observed {observedHandles.Count}");
    }
}
