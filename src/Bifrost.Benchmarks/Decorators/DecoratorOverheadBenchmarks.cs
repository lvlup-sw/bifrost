// =============================================================================
// <copyright file="DecoratorOverheadBenchmarks.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using BenchmarkDotNet.Attributes;
using Bifrost.Autoscaling;
using Bifrost.Benchmarks.Helpers;
using Bifrost.Core;
using Bifrost.Decorators;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bifrost.Benchmarks.Decorators;

/// <summary>
/// Measures per-layer cost by comparing bare vs decorated orchestrators.
/// Target: less than 20ns per decorator layer.
/// </summary>
[MemoryDiagnoser]
public class DecoratorOverheadBenchmarks
{
    private IWorkOrchestrator<int>? _bare;
    private IWorkOrchestrator<int>? _withAutoscaling;
    private IWorkOrchestrator<int>? _withEventStream;
    private IWorkOrchestrator<int>? _fullStack;

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

        var baseForAutoscaling = new WorkOrchestrator<int>(
            handler,
            options,
            NullLogger<WorkOrchestrator<int>>.Instance);
        _withAutoscaling = new AutoscalingOrchestrator<int>(
            baseForAutoscaling,
            new WorkerMetrics());

        var baseForEventStream = new WorkOrchestrator<int>(
            handler,
            options,
            NullLogger<WorkOrchestrator<int>>.Instance);
        _withEventStream = new EventStreamOrchestrator<int>(
            baseForEventStream,
            NullLogger<EventStreamOrchestrator<int>>.Instance);

        var baseForFullStack = new WorkOrchestrator<int>(
            handler,
            options,
            NullLogger<WorkOrchestrator<int>>.Instance);
        var autoscalingLayer = new AutoscalingOrchestrator<int>(
            baseForFullStack,
            new WorkerMetrics());
        _fullStack = new EventStreamOrchestrator<int>(
            autoscalingLayer,
            NullLogger<EventStreamOrchestrator<int>>.Instance);
    }

    /// <summary>
    /// Baseline: bare orchestrator TryEnqueue.
    /// </summary>
    /// <returns>Whether the enqueue succeeded.</returns>
    [Benchmark(Baseline = true)]
    public bool TryEnqueue_Bare() => _bare!.TryEnqueue(42);

    /// <summary>
    /// Bare orchestrator wrapped with autoscaling decorator.
    /// </summary>
    /// <returns>Whether the enqueue succeeded.</returns>
    [Benchmark]
    public bool TryEnqueue_WithAutoscaling() => _withAutoscaling!.TryEnqueue(42);

    /// <summary>
    /// Bare orchestrator wrapped with event stream decorator.
    /// </summary>
    /// <returns>Whether the enqueue succeeded.</returns>
    [Benchmark]
    public bool TryEnqueue_WithEventStream() => _withEventStream!.TryEnqueue(42);

    /// <summary>
    /// Full decorator stack: EventStream wrapping Autoscaling wrapping bare.
    /// </summary>
    /// <returns>Whether the enqueue succeeded.</returns>
    [Benchmark]
    public bool TryEnqueue_FullStack() => _fullStack!.TryEnqueue(42);

    /// <summary>
    /// Disposes orchestrator instances after each iteration.
    /// </summary>
    /// <returns>A task representing the asynchronous cleanup.</returns>
    [IterationCleanup]
    public async Task IterationCleanup()
    {
        if (_fullStack is not null)
        {
            await _fullStack.DisposeAsync().ConfigureAwait(false);
        }

        if (_withEventStream is not null)
        {
            await _withEventStream.DisposeAsync().ConfigureAwait(false);
        }

        if (_withAutoscaling is not null)
        {
            await _withAutoscaling.DisposeAsync().ConfigureAwait(false);
        }

        if (_bare is not null)
        {
            await _bare.DisposeAsync().ConfigureAwait(false);
        }
    }
}
