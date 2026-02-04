// =============================================================================
// <copyright file="EnqueueAllocationBenchmarks.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using BenchmarkDotNet.Attributes;
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
        var handler = new NoOpHandler();
        var options = Options.Create(new WorkOrchestratorOptions { Capacity = 10000, WorkerCount = 1 });
        var logger = NullLogger<WorkOrchestrator<int>>.Instance;

        _orchestrator = new WorkOrchestrator<int>(handler, options, logger);

        // Warmup: run 100 enqueue cycles to stabilize JIT and allocations.
        // Workers drain items instantly with no-op handler.
        for (var i = 0; i < 100; i++)
        {
            _orchestrator.TryEnqueue(i);
        }

        // Allow workers to drain warmup items
        await Task.Delay(50).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates that <c>TryEnqueue</c> allocates 0 bytes on the hot path.
    /// </summary>
    /// <returns><c>true</c> if the item was enqueued successfully.</returns>
    [Benchmark]
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

    private sealed class NoOpHandler : IWorkHandler<int>
    {
        public ValueTask HandleAsync(int work, CancellationToken ct) => ValueTask.CompletedTask;
    }
}
