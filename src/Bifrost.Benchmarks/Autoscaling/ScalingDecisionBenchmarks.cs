// =============================================================================
// <copyright file="ScalingDecisionBenchmarks.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using BenchmarkDotNet.Attributes;
using Bifrost.Autoscaling;
using Microsoft.Extensions.Options;

namespace Bifrost.Benchmarks.Autoscaling;

/// <summary>
/// Measures how fast the autoscaling engine evaluates watermarks and makes scaling decisions.
/// </summary>
[MemoryDiagnoser]
public class ScalingDecisionBenchmarks
{
    private AutoscalingEngine? _engine;

    /// <summary>
    /// Gets or sets the utilization ratio to evaluate.
    /// </summary>
    [Params(0.1, 0.5, 0.9)]
    public double Utilization { get; set; }

    /// <summary>
    /// Creates the autoscaling engine with zero cooldown for consistent benchmarking.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        var options = new AutoscalingOptions
        {
            CooldownPeriod = TimeSpan.Zero,
        };

        _engine = new AutoscalingEngine(new OptionsWrapper<AutoscalingOptions>(options));
    }

    /// <summary>
    /// Benchmarks a single scaling evaluation at the parameterized utilization level.
    /// </summary>
    /// <returns>The scaling decision result.</returns>
    [Benchmark]
    public ScalingDecision EvaluateScaling()
    {
        return _engine!.EvaluateScaling(currentWorkers: 5, Utilization, maxBacklog: 1000);
    }

    /// <summary>
    /// Disposes the autoscaling engine.
    /// </summary>
    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _engine?.Dispose();
    }
}
