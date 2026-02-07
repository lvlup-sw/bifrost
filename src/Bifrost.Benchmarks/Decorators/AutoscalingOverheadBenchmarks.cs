// =============================================================================
// <copyright file="AutoscalingOverheadBenchmarks.cs" company="Levelup Software">
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
/// Isolates autoscaling metrics collection cost by comparing
/// metrics-enabled vs metrics-disabled orchestrators across varying capacities.
/// </summary>
[MemoryDiagnoser]
public class AutoscalingOverheadBenchmarks
{
    private IWorkOrchestrator<int>? _metricsEnabled;
    private IWorkOrchestrator<int>? _metricsDisabled;

    /// <summary>
    /// Gets or sets the channel capacity for the underlying orchestrator.
    /// </summary>
    [Params(32, 128, 1024)]
    public int Capacity { get; set; }

    /// <summary>
    /// Creates two autoscaling orchestrator variants: metrics enabled and disabled.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        var options = new OptionsWrapper<WorkOrchestratorOptions>(
            new WorkOrchestratorOptions { Capacity = Capacity, WorkerCount = 1 });
        var handler = new NoOpWorkHandler();

        var baseForEnabled = new WorkOrchestrator<int>(
            handler,
            options,
            NullLogger<WorkOrchestrator<int>>.Instance);
        _metricsEnabled = new AutoscalingOrchestrator<int>(
            baseForEnabled,
            new WorkerMetrics());

        var baseForDisabled = new WorkOrchestrator<int>(
            handler,
            options,
            NullLogger<WorkOrchestrator<int>>.Instance);
        _metricsDisabled = new AutoscalingOrchestrator<int>(
            baseForDisabled,
            new WorkerRegistry(),
            new WorkerMetrics(),
            new AutoscalingOptions { Enabled = false },
            NullLogger<AutoscalingOrchestrator<int>>.Instance);
    }

    /// <summary>
    /// Autoscaling decorator with metrics collection enabled.
    /// </summary>
    /// <returns>Whether the enqueue succeeded.</returns>
    [Benchmark]
    public bool TryEnqueue_MetricsEnabled() => _metricsEnabled!.TryEnqueue(42);

    /// <summary>
    /// Autoscaling decorator with metrics collection disabled.
    /// </summary>
    /// <returns>Whether the enqueue succeeded.</returns>
    [Benchmark(Baseline = true)]
    public bool TryEnqueue_MetricsDisabled() => _metricsDisabled!.TryEnqueue(42);

    /// <summary>
    /// Disposes all orchestrator instances.
    /// </summary>
    /// <returns>A task representing the asynchronous cleanup.</returns>
    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        if (_metricsEnabled is not null)
        {
            await _metricsEnabled.DisposeAsync().ConfigureAwait(false);
        }

        if (_metricsDisabled is not null)
        {
            await _metricsDisabled.DisposeAsync().ConfigureAwait(false);
        }
    }
}
