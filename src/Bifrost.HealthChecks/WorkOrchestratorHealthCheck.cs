// =============================================================================
// <copyright file="WorkOrchestratorHealthCheck.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Levelup.Channels.Core;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Levelup.Channels.HealthChecks;

/// <summary>
/// Health check for monitoring <see cref="IWorkOrchestrator{TWork}"/> status.
/// </summary>
/// <typeparam name="TWork">The type of work item to process.</typeparam>
/// <remarks>
/// <para>
/// This health check monitors the orchestrator's queue utilization:
/// <list type="bullet">
///   <item><description><see cref="HealthStatus.Healthy"/> - Queue utilization at or below 95%</description></item>
///   <item><description><see cref="HealthStatus.Degraded"/> - Queue utilization above 95%</description></item>
/// </list>
/// </para>
/// <para>
/// The health check description includes the current pending count, capacity, and worker count
/// for diagnostic purposes.
/// </para>
/// </remarks>
public sealed class WorkOrchestratorHealthCheck<TWork> : IHealthCheck
{
    /// <summary>
    /// The utilization threshold above which the orchestrator is considered degraded.
    /// </summary>
    private const double DegradedThreshold = 0.95;

    private readonly IWorkOrchestrator<TWork> _orchestrator;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkOrchestratorHealthCheck{TWork}"/> class.
    /// </summary>
    /// <param name="orchestrator">The orchestrator to monitor.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="orchestrator"/> is null.</exception>
    public WorkOrchestratorHealthCheck(IWorkOrchestrator<TWork> orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        _orchestrator = orchestrator;
    }

    /// <summary>
    /// Checks the health of the work orchestrator.
    /// </summary>
    /// <param name="context">The health check context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The health check result.</returns>
    /// <remarks>
    /// Returns <see cref="HealthStatus.Degraded"/> when queue utilization exceeds 95%,
    /// otherwise returns <see cref="HealthStatus.Healthy"/>.
    /// </remarks>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var pendingCount = _orchestrator.PendingCount;
        var capacity = _orchestrator.Capacity;
        var activeWorkers = _orchestrator.ActiveWorkers;
        var utilization = capacity > 0 ? (double)pendingCount / capacity : 0;

        if (utilization > DegradedThreshold)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Queue nearly full: {pendingCount}/{capacity} ({utilization:P1}), Workers: {activeWorkers}"));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            $"Queue: {pendingCount}/{capacity} ({utilization:P1}), Workers: {activeWorkers}"));
    }
}
