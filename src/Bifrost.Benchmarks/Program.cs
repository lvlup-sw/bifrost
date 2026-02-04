// =============================================================================
// <copyright file="Program.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
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

var config = ManualConfig
    .Create(DefaultConfig.Instance)
    .WithOptions(ConfigOptions.DisableOptimizationsValidator);

var switcher = new BenchmarkSwitcher(
[
    typeof(DecoratorOverheadBenchmarks),
    typeof(AutoscalingOverheadBenchmarks),
    typeof(ResilienceOverheadBenchmarks),
]);

switcher.Run(args, config);
