// =============================================================================
// <copyright file="ResilienceOverheadBenchmarks.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using BenchmarkDotNet.Attributes;

using Bifrost.Benchmarks.Helpers;
using Bifrost.Core;
using Bifrost.Resilience;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bifrost.Benchmarks.Decorators;

/// <summary>
/// Measures Polly pipeline overhead on the happy path.
/// The resilience decorator allocates a lambda and Context on every call,
/// so this benchmark quantifies that cost.
/// </summary>
[MemoryDiagnoser]
public class ResilienceOverheadBenchmarks
{
    private IWorkOrchestrator<int>? _bare;
    private IWorkOrchestrator<int>? _withResilience;

    /// <summary>
    /// Creates bare and resilience-wrapped orchestrator variants.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        var options = new OptionsWrapper<WorkOrchestratorOptions>(
            new WorkOrchestratorOptions { Capacity = 10000, WorkerCount = 1 });
        var handler = new NoOpWorkHandler();

        _bare = new WorkOrchestrator<int>(
            handler,
            options,
            NullLogger<WorkOrchestrator<int>>.Instance);

        var baseForResilience = new WorkOrchestrator<int>(
            handler,
            options,
            NullLogger<WorkOrchestrator<int>>.Instance);
        _withResilience = new ResilientOrchestrator<int>(
            baseForResilience,
            new OptionsWrapper<ResiliencySettings>(new ResiliencySettings()),
            NullLogger<ResilientOrchestrator<int>>.Instance);
    }

    /// <summary>
    /// Baseline: bare orchestrator EnqueueAsync.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> representing the enqueue operation.</returns>
    [Benchmark(Baseline = true)]
    public ValueTask EnqueueAsync_Bare() => _bare!.EnqueueAsync(42);

    /// <summary>
    /// Resilience-wrapped orchestrator EnqueueAsync exercising the Polly pipeline.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> representing the enqueue operation.</returns>
    [Benchmark]
    public ValueTask EnqueueAsync_WithResilience() => _withResilience!.EnqueueAsync(42);

    /// <summary>
    /// Disposes all orchestrator instances.
    /// </summary>
    /// <returns>A task representing the asynchronous cleanup.</returns>
    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        if (_withResilience is not null)
        {
            await _withResilience.DisposeAsync().ConfigureAwait(false);
        }

        if (_bare is not null)
        {
            await _bare.DisposeAsync().ConfigureAwait(false);
        }
    }
}