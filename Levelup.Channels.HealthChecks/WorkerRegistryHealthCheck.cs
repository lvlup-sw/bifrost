// =============================================================================
// <copyright file="WorkerRegistryHealthCheck.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Levelup.Channels.Autoscaling;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Levelup.Channels.HealthChecks;

/// <summary>
/// Health check for monitoring <see cref="IWorkerRegistry"/> status.
/// </summary>
/// <remarks>
/// <para>
/// This health check monitors the worker registry's state:
/// <list type="bullet">
///   <item><description><see cref="HealthStatus.Unhealthy"/> - No active workers</description></item>
///   <item><description><see cref="HealthStatus.Degraded"/> - All workers are busy</description></item>
///   <item><description><see cref="HealthStatus.Healthy"/> - Workers have available capacity</description></item>
/// </list>
/// </para>
/// <para>
/// The health check data includes worker counts for diagnostic purposes:
/// ActiveWorkers, IdleWorkers, BusyWorkers, and StoppingWorkers.
/// </para>
/// </remarks>
public sealed class WorkerRegistryHealthCheck : IHealthCheck
{
    private readonly IWorkerRegistry _registry;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkerRegistryHealthCheck"/> class.
    /// </summary>
    /// <param name="registry">The worker registry to monitor.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="registry"/> is null.</exception>
    public WorkerRegistryHealthCheck(IWorkerRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <summary>
    /// Checks the health of the worker registry.
    /// </summary>
    /// <param name="context">The health check context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The health check result.</returns>
    /// <remarks>
    /// <para>
    /// Returns <see cref="HealthStatus.Unhealthy"/> when no workers are active.
    /// </para>
    /// <para>
    /// Returns <see cref="HealthStatus.Degraded"/> when all workers are busy.
    /// </para>
    /// <para>
    /// Returns <see cref="HealthStatus.Healthy"/> when workers have available capacity.
    /// </para>
    /// </remarks>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var activeWorkers = _registry.ActiveWorkerCount;
        var idleWorkers = _registry.IdleWorkerCount;
        var busyWorkers = activeWorkers - idleWorkers;

        // Count workers with stop requested
        var workers = _registry.GetAllWorkers();
        var stoppingWorkers = workers.Count(w => w.StopRequested);

        var data = CreateSnapshotData(activeWorkers, idleWorkers, busyWorkers, stoppingWorkers);

        // Check: No active workers
        if (activeWorkers == 0)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "No active workers",
                data: data));
        }

        // Check: All workers are busy
        if (idleWorkers == 0)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                "All workers are busy",
                data: data));
        }

        // Workers have capacity
        return Task.FromResult(HealthCheckResult.Healthy(
            $"Worker registry is healthy ({idleWorkers}/{activeWorkers} workers idle)",
            data: data));
    }

    private static Dictionary<string, object> CreateSnapshotData(
        int activeWorkers,
        int idleWorkers,
        int busyWorkers,
        int stoppingWorkers)
        => new()
        {
            ["ActiveWorkers"] = activeWorkers,
            ["IdleWorkers"] = idleWorkers,
            ["BusyWorkers"] = busyWorkers,
            ["StoppingWorkers"] = stoppingWorkers,
        };
}
