// =============================================================================
// <copyright file="EnqueueBenchmarks.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using BenchmarkDotNet.Attributes;
using Bifrost.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bifrost.Benchmarks.Core;

/// <summary>
/// Benchmarks measuring latency of all four enqueue paths on <see cref="WorkOrchestrator{TWork}"/>.
/// </summary>
[MemoryDiagnoser]
public class EnqueueBenchmarks
{
    private WorkOrchestrator<int>? _orchestrator;

    /// <summary>
    /// Gets or sets the channel capacity used for each benchmark run.
    /// </summary>
    [Params(128, 1024)]
    public int Capacity { get; set; }

    /// <summary>
    /// Creates the orchestrator with a no-op handler and a single worker.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        var handler = new NoOpWorkHandler();
        var options = Options.Create(new WorkOrchestratorOptions
        {
            Capacity = Capacity,
            WorkerCount = 1,
        });
        var logger = NullLogger<WorkOrchestrator<int>>.Instance;

        _orchestrator = new WorkOrchestrator<int>(handler, options, logger);
    }

    /// <summary>
    /// Benchmarks the synchronous try-enqueue path (baseline).
    /// </summary>
    /// <returns><see langword="true"/> if the item was enqueued.</returns>
    [Benchmark(Baseline = true)]
    public bool TryEnqueue()
    {
        return _orchestrator!.TryEnqueue(42);
    }

    /// <summary>
    /// Benchmarks the async enqueue path.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> representing the enqueue operation.</returns>
    [Benchmark]
    public ValueTask EnqueueAsync()
    {
        return _orchestrator!.EnqueueAsync(42);
    }

    /// <summary>
    /// Benchmarks the synchronous Run enqueue path.
    /// </summary>
    [Benchmark]
    public void Run_Sync()
    {
        _orchestrator!.Run(42);
    }

    /// <summary>
    /// Benchmarks the synchronous TryRun enqueue path.
    /// </summary>
    /// <returns><see langword="true"/> if the item was enqueued.</returns>
    [Benchmark]
    public bool TryRun_Sync()
    {
        return _orchestrator!.TryRun(42);
    }

    /// <summary>
    /// Disposes the orchestrator after all benchmarks complete.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> representing the cleanup operation.</returns>
    [GlobalCleanup]
    public ValueTask GlobalCleanup()
    {
        return _orchestrator?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private sealed class NoOpWorkHandler : IWorkHandler<int>
    {
        public ValueTask HandleAsync(int work, CancellationToken ct)
        {
            return ValueTask.CompletedTask;
        }
    }
}
