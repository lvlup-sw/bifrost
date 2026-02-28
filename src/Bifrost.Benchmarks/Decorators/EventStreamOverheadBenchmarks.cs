// =============================================================================
// <copyright file="EventStreamOverheadBenchmarks.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using BenchmarkDotNet.Attributes;

using Bifrost.Benchmarks.Helpers;
using Bifrost.Core;
using Bifrost.Decorators;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bifrost.Benchmarks.Decorators;

/// <summary>
/// Measures the overhead of the EventStream decorator on TryEnqueue.
/// Compares bare orchestrator vs orchestrator with EventStream.
/// </summary>
[MemoryDiagnoser]
public class EventStreamOverheadBenchmarks
{
    private IWorkOrchestrator<int>? _bare;
    private IWorkOrchestrator<int>? _withEventStream;

    /// <summary>
    /// Creates fresh orchestrator variants for each benchmark iteration.
    /// </summary>
    [IterationSetup]
    public void IterationSetup()
    {
        var options = new OptionsWrapper<WorkOrchestratorOptions>(
            new WorkOrchestratorOptions { Capacity = 10000, WorkerCount = 1 });
        var handler = new NoOpWorkHandler();

        _bare = new WorkOrchestrator<int>(
            handler,
            options,
            NullLogger<WorkOrchestrator<int>>.Instance);

        var baseForEventStream = new WorkOrchestrator<int>(
            handler,
            options,
            NullLogger<WorkOrchestrator<int>>.Instance);
        _withEventStream = new EventStreamOrchestrator<int>(
            baseForEventStream,
            NullLogger<EventStreamOrchestrator<int>>.Instance);
    }

    /// <summary>
    /// Baseline: bare orchestrator TryEnqueue.
    /// </summary>
    /// <returns>Whether the enqueue succeeded.</returns>
    [Benchmark(Baseline = true)]
    public bool TryEnqueue_Bare() => _bare!.TryEnqueue(42);

    /// <summary>
    /// Bare orchestrator wrapped with event stream decorator.
    /// </summary>
    /// <returns>Whether the enqueue succeeded.</returns>
    [Benchmark]
    public bool TryEnqueue_WithEventStream() => _withEventStream!.TryEnqueue(42);

    /// <summary>
    /// Disposes orchestrator instances after each iteration.
    /// </summary>
    [IterationCleanup]
    public void IterationCleanup()
    {
#pragma warning disable VSTHRD002 // BenchmarkDotNet IterationCleanup must be synchronous
        _withEventStream?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _bare?.DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
    }
}
