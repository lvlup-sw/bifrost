// =============================================================================
// <copyright file="AutoscalingHealthCheck.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Bifrost.HealthChecks;

/// <summary>
/// Health check for monitoring autoscaling state and capacity.
/// </summary>
/// <remarks>
/// <para>
/// This health check monitors the autoscaling system's state:
/// <list type="bullet">
///   <item><description><see cref="HealthStatus.Unhealthy"/> - No active workers</description></item>
///   <item><description><see cref="HealthStatus.Degraded"/> - At maximum or minimum worker capacity with no idle workers</description></item>
///   <item><description><see cref="HealthStatus.Healthy"/> - Workers within normal range with available capacity</description></item>
/// </list>
/// </para>
/// <para>
/// The health check data includes ActiveWorkers, MinWorkers, and MaxWorkers
/// for diagnostic purposes.
/// </para>
/// </remarks>
public sealed class AutoscalingHealthCheck : IHealthCheck
{
    private readonly IWorkerRegistry _registry;
    private readonly AutoscalingOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoscalingHealthCheck"/> class.
    /// </summary>
    /// <param name="registry">The worker registry to monitor.</param>
    /// <param name="options">The autoscaling configuration options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="registry"/> or <paramref name="options"/> is null.</exception>
    public AutoscalingHealthCheck(IWorkerRegistry registry, IOptions<AutoscalingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);

        _registry = registry;
        _options = options.Value;
    }

    /// <summary>
    /// Checks the health of the autoscaling system.
    /// </summary>
    /// <param name="context">The health check context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The health check result.</returns>
    /// <remarks>
    /// <para>
    /// Returns <see cref="HealthStatus.Unhealthy"/> when no workers are active.
    /// </para>
    /// <para>
    /// Returns <see cref="HealthStatus.Degraded"/> when at maximum worker capacity
    /// or at minimum with all workers busy.
    /// </para>
    /// <para>
    /// Returns <see cref="HealthStatus.Healthy"/> when workers are within normal
    /// scaling range with available capacity.
    /// </para>
    /// </remarks>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var activeWorkers = _registry.ActiveWorkerCount;
        var idleWorkers = _registry.IdleWorkerCount;

        var data = CreateAutoscalingData(activeWorkers, _options.MinWorkers, _options.MaxWorkers);

        // Check: No active workers
        if (activeWorkers == 0)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "No active workers",
                data: data));
        }

        // Check: At maximum capacity
        if (activeWorkers >= _options.MaxWorkers)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"At maximum worker capacity ({activeWorkers}/{_options.MaxWorkers})",
                data: data));
        }

        // Check: At minimum workers with all busy (can't scale down and no capacity)
        if (activeWorkers <= _options.MinWorkers && idleWorkers == 0)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"At minimum workers ({activeWorkers}) with no idle capacity",
                data: data));
        }

        // Healthy - within normal range
        return Task.FromResult(HealthCheckResult.Healthy(
            $"Autoscaling healthy: {activeWorkers} workers (min: {_options.MinWorkers}, max: {_options.MaxWorkers})",
            data: data));
    }

    private static Dictionary<string, object> CreateAutoscalingData(
        int activeWorkers,
        int minWorkers,
        int maxWorkers)
        => new()
        {
            ["ActiveWorkers"] = activeWorkers,
            ["MinWorkers"] = minWorkers,
            ["MaxWorkers"] = maxWorkers,
        };
}