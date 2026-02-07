// =============================================================================
// <copyright file="Program.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

using Bifrost.Benchmarks.Allocation;
using Bifrost.Benchmarks.Autoscaling;
using Bifrost.Benchmarks.Core;
using Bifrost.Benchmarks.Decorators;

// Use BenchmarkSwitcher for flexible benchmark selection
// Run with --filter to select specific benchmarks, e.g.:
//   dotnet run -c Release -- --filter "*EnqueueBenchmarks*"
//   dotnet run -c Release -- --filter "*Allocation*"
//
// List all available benchmarks:
//   dotnet run -c Release -- --list flat
//
// CI smoke test (minimal run):
//   dotnet run -c Release -- --job Dry --filter "*"

var config = ManualConfig.Create(DefaultConfig.Instance);

if (Array.Exists(args, a => a.Equals("Dry", StringComparison.OrdinalIgnoreCase)))
{
    config = config.WithOptions(ConfigOptions.DisableOptimizationsValidator);
}

var switcher = new BenchmarkSwitcher(
[
    typeof(EnqueueBenchmarks),
    typeof(SyncEnqueueBenchmarks),
    typeof(WorkerThroughputBenchmarks),
    typeof(EnqueueAllocationBenchmarks),
    typeof(WorkerLoopAllocationBenchmarks),
    typeof(EventStreamAllocationBenchmarks),
    typeof(DecoratorOverheadBenchmarks),
    typeof(AutoscalingOverheadBenchmarks),
    typeof(ResilienceOverheadBenchmarks),
    typeof(ScalingDecisionBenchmarks),
    typeof(MetricsCollectionBenchmarks),
]);

switcher.Run(args, config);