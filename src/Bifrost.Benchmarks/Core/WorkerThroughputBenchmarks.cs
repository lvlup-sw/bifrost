// =============================================================================
// <copyright file="WorkerThroughputBenchmarks.cs" company="Levelup Software">
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
/// Benchmarks measuring worker throughput (items/sec) for <see cref="WorkOrchestrator{TWork}"/>.
/// </summary>
[MemoryDiagnoser]
public class WorkerThroughputBenchmarks
{
    private const int ItemCount = 100_000;

    private WorkOrchestrator<int>? _orchestrator;
    private CountdownEvent? _countdown;

    /// <summary>
    /// Gets or sets the number of concurrent workers processing items.
    /// </summary>
    [Params(1, 4, 16)]
    public int WorkerCount { get; set; }

    /// <summary>
    /// Creates the orchestrator with a handler that signals a countdown event.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        _countdown = new CountdownEvent(ItemCount);

        var handler = new CountdownWorkHandler(_countdown);
        var options = Options.Create(new WorkOrchestratorOptions
        {
            Capacity = Math.Min(ItemCount, 10_000),
            WorkerCount = WorkerCount,
        });
        var logger = NullLogger<WorkOrchestrator<int>>.Instance;

        _orchestrator = new WorkOrchestrator<int>(handler, options, logger);
    }

    /// <summary>
    /// Resets the countdown event before each iteration.
    /// </summary>
    [IterationSetup]
    public void IterationSetup()
    {
        _countdown!.Reset(ItemCount);
    }

    /// <summary>
    /// Enqueues <see cref="ItemCount"/> items and waits for all to be processed.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the benchmark operation.</returns>
    [Benchmark]
    public async Task Throughput()
    {
        for (var i = 0; i < ItemCount; i++)
        {
            await _orchestrator!.EnqueueAsync(i).ConfigureAwait(false);
        }

        if (!_countdown!.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("Workers did not process all items within timeout.");
        }
    }

    /// <summary>
    /// Disposes the orchestrator and countdown event after all benchmarks complete.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> representing the cleanup operation.</returns>
    [GlobalCleanup]
    public async ValueTask GlobalCleanup()
    {
        if (_orchestrator is not null)
        {
            await _orchestrator.DisposeAsync().ConfigureAwait(false);
        }

        _countdown?.Dispose();
    }
}
