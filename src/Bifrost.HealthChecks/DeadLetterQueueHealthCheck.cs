// =============================================================================
// <copyright file="DeadLetterQueueHealthCheck.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core.DeadLetter;

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Bifrost.HealthChecks;

/// <summary>
/// Health check that monitors dead letter queue depth.
/// </summary>
/// <typeparam name="TWork">The type of work item.</typeparam>
/// <remarks>
/// <para>
/// Reports health status based on DLQ item count:
/// <list type="bullet">
///   <item><description><see cref="HealthStatus.Healthy"/> - Below 100 items</description></item>
///   <item><description><see cref="HealthStatus.Degraded"/> - 100 to 999 items</description></item>
///   <item><description><see cref="HealthStatus.Unhealthy"/> - 1000 or more items</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class DeadLetterQueueHealthCheck<TWork> : IHealthCheck
{
    private const int DegradedThreshold = 100;
    private const int UnhealthyThreshold = 1000;

    private readonly IDeadLetterQueue<TWork> _dlq;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeadLetterQueueHealthCheck{TWork}"/> class.
    /// </summary>
    /// <param name="dlq">The dead letter queue to monitor.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="dlq"/> is null.</exception>
    public DeadLetterQueueHealthCheck(IDeadLetterQueue<TWork> dlq)
    {
        ArgumentNullException.ThrowIfNull(dlq);
        _dlq = dlq;
    }

    /// <summary>
    /// Checks the health of the dead letter queue.
    /// </summary>
    /// <param name="context">The health check context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The health check result.</returns>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var count = _dlq.Count;

        if (count >= UnhealthyThreshold)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Dead letter queue has {count} items (threshold: {UnhealthyThreshold})"));
        }

        if (count >= DegradedThreshold)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Dead letter queue has {count} items (threshold: {DegradedThreshold})"));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            $"Dead letter queue has {count} items"));
    }
}