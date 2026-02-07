// =============================================================================
// <copyright file="MetricsCollectionBenchmarks.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using BenchmarkDotNet.Attributes;

using Bifrost.Autoscaling;

namespace Bifrost.Benchmarks.Autoscaling;

/// <summary>
/// Measures the cost of WorkerMetrics Interlocked operations.
/// Target: less than 10ns per operation.
/// </summary>
[MemoryDiagnoser]
public class MetricsCollectionBenchmarks
{
    private WorkerMetrics? _metrics;

    /// <summary>
    /// Creates a WorkerMetrics instance and pre-seeds it with enqueue calls.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        _metrics = new WorkerMetrics();

        // Pre-seed with pending work so CalculateUtilizationRatio has data
        for (var i = 0; i < 100; i++)
        {
            _metrics.RecordEnqueue();
        }
    }

    /// <summary>
    /// Benchmarks the cost of recording an enqueue operation.
    /// </summary>
    [Benchmark]
    public void RecordEnqueue()
    {
        _metrics!.RecordEnqueue();
    }

    /// <summary>
    /// Benchmarks the cost of recording a dequeue operation.
    /// </summary>
    [Benchmark]
    public void RecordDequeue()
    {
        _metrics!.RecordDequeue();
    }

    /// <summary>
    /// Benchmarks the cost of calculating the utilization ratio.
    /// </summary>
    /// <returns>The utilization ratio.</returns>
    [Benchmark]
    public double CalculateUtilization()
    {
        return _metrics!.CalculateUtilizationRatio(1000);
    }
}