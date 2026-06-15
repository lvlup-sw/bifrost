// =============================================================================
// <copyright file="SchedulerHealthCheck.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Bifrost.Scheduling.Observability;

/// <summary>
/// An <see cref="IHealthCheck"/> for the durable scheduler (DR-8). The scheduler is
/// reported <see cref="HealthStatus.Unhealthy"/> when any of three liveness
/// conditions holds, and <see cref="HealthStatus.Healthy"/> otherwise:
/// <list type="number">
///   <item><description>the scheduler is in its faulted state — its tick loop crashed too many times and stopped (DR-10);</description></item>
///   <item><description>no tick has occurred for longer than three times the expected tick interval, indicating a stalled loop;</description></item>
///   <item><description>the dispatch-failure rate over the last 100 fires exceeds the failure-rate threshold (default 50%).</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Registration is wired automatically by <c>AddScheduler</c> through a factory
/// lambda — <c>AddHealthChecks().Add(new HealthCheckRegistration("bifrost.scheduling",
/// sp =&gt; new SchedulerHealthCheck(...), HealthStatus.Unhealthy, tags: ["scheduling"]))</c>
/// — which resolves the monitor, fault source, and time provider from the service
/// provider and supplies the expected tick interval.
/// </para>
/// <para>
/// The reflection-free <c>HealthCheckRegistration</c> factory is used deliberately
/// instead of <c>AddCheck&lt;SchedulerHealthCheck&gt;(...)</c>: this check's
/// constructor is intentionally <see langword="internal"/> (it takes the expected
/// interval and infrastructure dependencies that are not consumer-visible), and the
/// generic <c>AddCheck&lt;T&gt;()</c> overload activates <c>T</c> via reflection,
/// which is both unable to reach an internal constructor and unsafe under
/// trimming/NativeAOT. The factory closes over the statically-known constructor, so
/// it is AOT-safe and needs no public surface.
/// </para>
/// </remarks>
public sealed class SchedulerHealthCheck : IHealthCheck
{
    /// <summary>
    /// The default dispatch-failure-rate threshold above which the scheduler is
    /// reported unhealthy.
    /// </summary>
    public const double DefaultFailureRateThreshold = 0.5;

    /// <summary>
    /// The multiple of the expected tick interval beyond which a missing tick is
    /// treated as a stalled loop.
    /// </summary>
    private const int StaleTickIntervalMultiplier = 3;

    private readonly ITickHealthMonitor monitor;
    private readonly ISchedulerFaultSource faultSource;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan expectedInterval;
    private readonly double failureRateThreshold;

    /// <summary>
    /// Initializes a new instance of the <see cref="SchedulerHealthCheck"/> class.
    /// </summary>
    /// <param name="monitor">The tick/fire liveness monitor the loop updates.</param>
    /// <param name="faultSource">The scheduler's faulted-state source (the tick loop).</param>
    /// <param name="timeProvider">The clock used to evaluate tick staleness (DR-7).</param>
    /// <param name="expectedInterval">
    /// The expected interval between ticks; a gap exceeding three times this value is
    /// treated as a stalled loop.
    /// </param>
    /// <param name="failureRateThreshold">
    /// The dispatch-failure-rate threshold above which the scheduler is unhealthy;
    /// defaults to <see cref="DefaultFailureRateThreshold"/>.
    /// </param>
    internal SchedulerHealthCheck(
        ITickHealthMonitor monitor,
        ISchedulerFaultSource faultSource,
        TimeProvider timeProvider,
        TimeSpan expectedInterval,
        double failureRateThreshold = DefaultFailureRateThreshold)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(faultSource);
        ArgumentNullException.ThrowIfNull(timeProvider);

        // A non-positive interval makes the staleness window (3x the interval)
        // meaningless, and a failure rate is a fraction — reject both at construction
        // rather than silently degrading the health signal at evaluation time.
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expectedInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(failureRateThreshold);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(failureRateThreshold, 1.0);

        this.monitor = monitor;
        this.faultSource = faultSource;
        this.timeProvider = timeProvider;
        this.expectedInterval = expectedInterval;
        this.failureRateThreshold = failureRateThreshold;
    }

    /// <summary>
    /// Evaluates the scheduler's health.
    /// </summary>
    /// <param name="context">The health check context.</param>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <returns>The health result: unhealthy on fault, stall, or excessive failures.</returns>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (this.faultSource.IsFaulted)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Scheduler is faulted: the tick loop crashed too many times in its restart window and stopped ticking."));
        }

        var now = this.timeProvider.GetUtcNow();
        var lastTick = this.monitor.LastTickAt;
        if (lastTick is not null)
        {
            var staleAfter = this.expectedInterval * StaleTickIntervalMultiplier;
            var sinceLastTick = now - lastTick.Value;
            if (sinceLastTick > staleAfter)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"Scheduler tick is stale: last tick was {sinceLastTick} ago, exceeding " +
                    $"{StaleTickIntervalMultiplier}x the expected interval ({this.expectedInterval})."));
            }
        }

        var failureRate = this.monitor.FailureRate;
        if (failureRate > this.failureRateThreshold)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Scheduler dispatch-failure rate {failureRate:P1} over the last fires exceeds " +
                $"the {this.failureRateThreshold:P0} threshold."));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            $"Scheduler healthy: dispatch-failure rate {failureRate:P1}, last tick at " +
            $"{(lastTick is null ? "never" : lastTick.Value.ToString("O"))}."));
    }
}
