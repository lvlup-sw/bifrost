// =============================================================================
// <copyright file="SyncEnqueueBenchmarks.cs" company="Levelup Software">
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
/// Benchmarks measuring latency of synchronous Run/TryRun paths on <see cref="WorkOrchestrator{TWork}"/>.
/// </summary>
/// <remarks>
/// These benchmarks use <see cref="IterationSetupAttribute"/> to create a fresh orchestrator per iteration,
/// avoiding <see cref="InvalidOperationException"/> when the channel fills under sustained BenchmarkDotNet load.
/// This forces InvocationCount=1, making absolute values less precise, but ratios remain meaningful.
/// </remarks>
[MemoryDiagnoser]
public class SyncEnqueueBenchmarks
{
    private WorkOrchestrator<int>? _orchestrator;

    /// <summary>
    /// Creates a fresh orchestrator for each benchmark iteration.
    /// </summary>
    [IterationSetup]
    public void IterationSetup()
    {
        var handler = new NoOpWorkHandler();
        var options = new OptionsWrapper<WorkOrchestratorOptions>(
            new WorkOrchestratorOptions { Capacity = 10000, WorkerCount = 1 });

        _orchestrator = new WorkOrchestrator<int>(
            handler,
            options,
            NullLogger<WorkOrchestrator<int>>.Instance);
    }

    /// <summary>
    /// Benchmarks the synchronous TryRun enqueue path (baseline).
    /// </summary>
    /// <returns><see langword="true"/> if the item was enqueued and processed.</returns>
    [Benchmark(Baseline = true)]
    public bool TryRun_Sync()
    {
        return _orchestrator!.TryRun(42);
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
    /// Disposes the orchestrator after each iteration.
    /// </summary>
    [IterationCleanup]
    public void IterationCleanup()
    {
#pragma warning disable VSTHRD002 // BenchmarkDotNet IterationCleanup must be synchronous
        _orchestrator?.DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
    }
}