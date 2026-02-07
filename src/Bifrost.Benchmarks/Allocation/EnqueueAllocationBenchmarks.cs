// =============================================================================
// <copyright file="EnqueueAllocationBenchmarks.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using BenchmarkDotNet.Attributes;

using Bifrost.Benchmarks.Helpers;
using Bifrost.Core;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bifrost.Benchmarks.Allocation;

/// <summary>
/// Validates zero-allocation on enqueue hot paths.
/// </summary>
/// <remarks>
/// The <see cref="WorkOrchestrator{TWork}"/> is designed to provide a zero-allocation
/// hot path for both <see cref="TryEnqueue_ZeroAlloc"/> and <see cref="EnqueueAsync_ZeroAlloc"/>
/// (synchronous fast path when capacity is available). These benchmarks verify that claim
/// using BenchmarkDotNet's memory diagnoser.
/// </remarks>
[MemoryDiagnoser]
public class EnqueueAllocationBenchmarks
{
    private WorkOrchestrator<int>? _orchestrator;

    /// <summary>
    /// Creates the orchestrator and runs a warmup phase to stabilize JIT and allocations.
    /// </summary>
    [GlobalSetup]
    public async Task GlobalSetup()
    {
        const int warmupCount = 100;
        var options = Options.Create(new WorkOrchestratorOptions { Capacity = 10000, WorkerCount = 1 });
        var logger = NullLogger<WorkOrchestrator<int>>.Instance;

        // Warmup: use a countdown handler to reliably confirm all items are processed,
        // stabilizing JIT and async state machines before the real benchmark begins.
        using var countdown = new CountdownEvent(warmupCount);
        var warmupHandler = new CountdownWorkHandler(countdown);
        await using (var warmup = new WorkOrchestrator<int>(warmupHandler, options, logger))
        {
            for (var i = 0; i < warmupCount; i++)
            {
                warmup.TryEnqueue(i);
            }

            if (!countdown.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Warmup items were not drained within the timeout.");
            }
        }

        // Create the benchmark orchestrator with the no-op handler for allocation measurement.
        var handler = new NoOpWorkHandler();
        _orchestrator = new WorkOrchestrator<int>(handler, options, logger);
    }

    /// <summary>
    /// Validates that <c>TryEnqueue</c> allocates 0 bytes on the hot path.
    /// </summary>
    /// <returns><c>true</c> if the item was enqueued successfully.</returns>
    [Benchmark(Baseline = true)]
    public bool TryEnqueue_ZeroAlloc()
    {
        return _orchestrator!.TryEnqueue(42);
    }

    /// <summary>
    /// Validates that <c>EnqueueAsync</c> allocates 0 bytes on the synchronous fast path
    /// when capacity is available.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> that completes synchronously when capacity is available.</returns>
    [Benchmark]
    public ValueTask EnqueueAsync_ZeroAlloc()
    {
        return _orchestrator!.EnqueueAsync(42);
    }

    /// <summary>
    /// Disposes the orchestrator and releases resources.
    /// </summary>
    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        if (_orchestrator is not null)
        {
            await _orchestrator.DisposeAsync().ConfigureAwait(false);
        }
    }
}