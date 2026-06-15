// =============================================================================
// <copyright file="PriorityWaitAllocationBenchmarks.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using BenchmarkDotNet.Attributes;

using Bifrost.Core;
using Bifrost.Queues;

namespace Bifrost.Benchmarks.Allocation;

/// <summary>
/// Measures the per-wait allocation of the priority bindings' park → wake path (DR-3,
/// task-6, issue #21): <c>ConcurrentPriorityWorkQueue.WaitToDequeueAsync</c> parks a
/// consumer on its <see cref="SemaphoreSlim"/> when the queue is empty and
/// non-completed, then a producer enqueue releases it.
/// </summary>
/// <remarks>
/// <para>
/// The park path of an <c>async ValueTask&lt;bool&gt;</c> heap-allocates the async
/// state-machine box on each suspension. Applying
/// <c>[AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder&lt;bool&gt;))]</c> to
/// <c>WaitToDequeueAsync</c> amortizes that box via the runtime's per-thread pool, so the
/// steady-state park-path allocation drops toward zero. This benchmark exercises exactly
/// one park → wake cycle per operation and reports the bytes allocated by it.
/// </para>
/// <para>
/// Each iteration: a consumer task issues <c>WaitToDequeueAsync</c> on the empty queue
/// (parks on the semaphore), the producer enqueues one item (releases the permit), and
/// the consumer drains it — restoring the queue to empty for the next cycle. A short
/// spin lets the consumer reach the park before the enqueue so the wait actually
/// suspends rather than completing synchronously.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class PriorityWaitAllocationBenchmarks
{
    /// <summary>
    /// Park → wake cycles per benchmark invocation; amortizes fixed per-invocation cost
    /// so the reported figure approaches the marginal per-wait allocation.
    /// </summary>
    private const int Cycles = 1000;

    /// <summary>
    /// Timestamp frequency used to construct the binding (1 kHz — one tick per ms).
    /// </summary>
    private const long TestTimestampFrequency = 1_000;

    private ConcurrentPriorityWorkQueue<int>? _queue;

    /// <summary>
    /// Builds the MultiQueue priority binding and warms the park → wake path so the JIT
    /// and (when enabled) the pooled async box are stabilized before measurement.
    /// </summary>
    /// <returns>A task representing the asynchronous setup.</returns>
    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _queue = new ConcurrentPriorityWorkQueue<int>(
            capacity: 1024,
            options: new PriorityDispatchOptions(),
            timestampFrequency: TestTimestampFrequency);

        // Warm up: run the cycle a few times to JIT the state machine and prime the
        // PoolingAsyncValueTaskMethodBuilder's per-thread box cache.
        for (var i = 0; i < 64; i++)
        {
            await ParkWakeCycleAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs <see cref="Cycles"/> park → wake cycles; the reported allocation divided by
    /// <see cref="Cycles"/> is the marginal per-wait allocation of the parked path.
    /// </summary>
    /// <returns>A task representing the asynchronous benchmark body.</returns>
    [Benchmark(OperationsPerInvoke = Cycles)]
    public async Task ParkWake_SteadyState()
    {
        for (var i = 0; i < Cycles; i++)
        {
            await ParkWakeCycleAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Disposes the binding's owned semaphore.
    /// </summary>
    [GlobalCleanup]
    public void GlobalCleanup() => _queue?.Dispose();

    /// <summary>
    /// One park → wake cycle: start the wait on the empty queue (parks on the
    /// semaphore), enqueue one item to release the permit, await the wait, then drain
    /// the item so the queue is empty for the next cycle.
    /// </summary>
    /// <returns>A task representing the asynchronous cycle.</returns>
    private async Task ParkWakeCycleAsync()
    {
        var queue = _queue!;

        // Start the wait first; on an empty, non-completed queue it parks on the
        // semaphore (the path that allocates the async box without pooling).
        var waitTask = queue.WaitToDequeueAsync(CancellationToken.None);

        // Spin briefly to let the consumer reach the park before the release, so the
        // wait genuinely suspends rather than completing synchronously.
        var spin = new SpinWait();
        while (!spin.NextSpinWillYield)
        {
            spin.SpinOnce();
        }

        // Release the parked waiter.
        queue.TryEnqueue(new WorkEnvelope<int>(0, WorkClass.Interactive, 0));

        await waitTask.ConfigureAwait(false);

        // Drain so the queue is empty again for the next cycle.
        queue.TryDequeue(out _);
    }
}
