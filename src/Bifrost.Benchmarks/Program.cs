// =============================================================================
// <copyright file="Program.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

using Bifrost.Benchmarks.Allocation;
using Bifrost.Benchmarks.Autoscaling;
using Bifrost.Benchmarks.Concurrency;
using Bifrost.Benchmarks.Core;
using Bifrost.Benchmarks.DeadLetter;
using Bifrost.Benchmarks.Decorators;
using Bifrost.Benchmarks.HealthChecks;
using Bifrost.Benchmarks.Orchestrator;

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
//
// CPQ contended-throughput verbs (ported from DataFerry@2bf0456): BenchmarkDotNet deliberately
// does not measure cross-thread throughput, so the custom fixed-window harness runs behind
// dedicated verbs instead of the switcher:
//   dotnet run -c Release -- throughput [windowSeconds] [outputDirectory]
//   dotnet run -c Release -- stickiness [windowSeconds] [outputDirectory]
if (args.Length > 0 && args[0].Equals("throughput", StringComparison.OrdinalIgnoreCase))
{
    ThroughputVerbs.RunThroughputSweep(args);
    return;
}

if (args.Length > 0 && args[0].Equals("stickiness", StringComparison.OrdinalIgnoreCase))
{
    ThroughputVerbs.RunStickinessSweep(args);
    return;
}

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
    typeof(PropertyAccessBenchmarks),
    typeof(EventStreamOverheadBenchmarks),
    typeof(WorkerRegistryBenchmarks),
    typeof(HealthCheckBenchmarks),
    typeof(DeadLetterQueueBenchmarks),
    typeof(OrchestratorBaselineBenchmarks),
    typeof(CpqSingleThreadedLatencyBenchmarks),
]);

switcher.Run(args, config);