// =============================================================================
// <copyright file="EnqueueBenchmarks.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using BenchmarkDotNet.Attributes;

using Bifrost.Benchmarks.Helpers;
using Bifrost.Core;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bifrost.Benchmarks.Core;

/// <summary>
/// Benchmarks measuring latency of async enqueue paths on <see cref="WorkOrchestrator{TWork}"/>.
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
    /// <remarks>
    /// <para>
    /// At <c>Capacity=128</c>, <see cref="System.Threading.Channels.ChannelWriter{T}.WriteAsync"/>
    /// may complete asynchronously under sustained load, causing the <see cref="ValueTask"/> to allocate
    /// (observed as ~1 B per call). This is .NET <c>Channel&lt;T&gt;</c> runtime behavior when the bounded
    /// channel is near capacity, not a Bifrost issue.
    /// </para>
    /// <para>
    /// At <c>Capacity=1024</c>, the channel rarely experiences backpressure and <c>WriteAsync</c> completes
    /// synchronously with zero allocation. For allocation-free async enqueue in hot paths, use
    /// <c>Capacity &gt;= 1024</c>.
    /// </para>
    /// </remarks>
    [Benchmark]
    public ValueTask EnqueueAsync()
    {
        return _orchestrator!.EnqueueAsync(42);
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
}