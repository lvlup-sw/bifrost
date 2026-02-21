// =============================================================================
// <copyright file="HealthCheckBenchmarks.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using BenchmarkDotNet.Attributes;

using Bifrost.Autoscaling;
using Bifrost.Benchmarks.Helpers;
using Bifrost.Core;
using Bifrost.HealthChecks;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bifrost.Benchmarks.HealthChecks;

/// <summary>
/// Measures health check execution latency.
/// </summary>
[MemoryDiagnoser]
public class HealthCheckBenchmarks
{
    private WorkOrchestratorHealthCheck<int>? _orchestratorHealthCheck;
    private AutoscalingHealthCheck? _autoscalingHealthCheck;
    private WorkOrchestrator<int>? _orchestrator;
    private HealthCheckContext? _context;

    /// <summary>
    /// Creates health check instances with their dependencies.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        var handler = new NoOpWorkHandler();
        var options = Options.Create(new WorkOrchestratorOptions
        {
            Capacity = 128,
            WorkerCount = 2,
        });
        var logger = NullLogger<WorkOrchestrator<int>>.Instance;

        _orchestrator = new WorkOrchestrator<int>(handler, options, logger);
        _orchestratorHealthCheck = new WorkOrchestratorHealthCheck<int>(_orchestrator);

        var registry = new WorkerRegistry();
        var autoscalingOptions = Options.Create(new AutoscalingOptions
        {
            MinWorkers = 1,
            MaxWorkers = 16,
        });
        _autoscalingHealthCheck = new AutoscalingHealthCheck(registry, autoscalingOptions);

        _context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("test", _ => null!, null, null),
        };
    }

    /// <summary>
    /// Benchmarks WorkOrchestratorHealthCheck execution.
    /// </summary>
    /// <returns>The health check result.</returns>
    [Benchmark]
    public Task<HealthCheckResult> WorkOrchestratorHealthCheck_Execute()
    {
        return _orchestratorHealthCheck!.CheckHealthAsync(_context!);
    }

    /// <summary>
    /// Benchmarks AutoscalingHealthCheck execution.
    /// </summary>
    /// <returns>The health check result.</returns>
    [Benchmark]
    public Task<HealthCheckResult> AutoscalingHealthCheck_Execute()
    {
        return _autoscalingHealthCheck!.CheckHealthAsync(_context!);
    }

    /// <summary>
    /// Disposes the orchestrator after all benchmarks complete.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> representing the cleanup operation.</returns>
    [GlobalCleanup]
    public ValueTask GlobalCleanup()
    {
        return _orchestrator?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}