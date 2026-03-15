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
    /// Creates a WorkerMetrics instance.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        _metrics = new WorkerMetrics();
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
    /// Benchmarks the cost of recording an execution start.
    /// </summary>
    [Benchmark]
    public void RecordExecutionStart()
    {
        _metrics!.RecordExecutionStart();
    }

    /// <summary>
    /// Benchmarks the cost of recording an execution end.
    /// </summary>
    [Benchmark]
    public void RecordExecutionEnd()
    {
        _metrics!.RecordExecutionEnd();
    }
}
