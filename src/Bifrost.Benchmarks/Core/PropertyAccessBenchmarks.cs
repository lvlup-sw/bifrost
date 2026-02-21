// =============================================================================
// <copyright file="PropertyAccessBenchmarks.cs" company="Levelup Software">
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
/// Measures the cost of accessing <see cref="IWorkOrchestrator{TWork}"/> properties.
/// Target: zero allocation, sub-nanosecond access.
/// </summary>
[MemoryDiagnoser]
public class PropertyAccessBenchmarks
{
    private WorkOrchestrator<int>? _orchestrator;

    /// <summary>
    /// Creates the orchestrator with a no-op handler.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        var handler = new NoOpWorkHandler();
        var options = Options.Create(new WorkOrchestratorOptions
        {
            Capacity = 128,
            WorkerCount = 2,
        });
        var logger = NullLogger<WorkOrchestrator<int>>.Instance;

        _orchestrator = new WorkOrchestrator<int>(handler, options, logger);
    }

    /// <summary>
    /// Benchmarks the PendingCount property access (baseline).
    /// </summary>
    /// <returns>The pending count value.</returns>
    [Benchmark(Baseline = true)]
    public int PendingCount() => _orchestrator!.PendingCount;

    /// <summary>
    /// Benchmarks the ActiveWorkers property access.
    /// </summary>
    /// <returns>The active workers value.</returns>
    [Benchmark]
    public int ActiveWorkers() => _orchestrator!.ActiveWorkers;

    /// <summary>
    /// Benchmarks the Capacity property access.
    /// </summary>
    /// <returns>The capacity value.</returns>
    [Benchmark]
    public int Capacity() => _orchestrator!.Capacity;

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