// =============================================================================
// <copyright file="SchedulerMetrics.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.Metrics;

using Bifrost.Scheduling.Core;

namespace Bifrost.Scheduling.Observability;

/// <summary>
/// Owns the scheduler's <see cref="Meter"/> and the
/// <see cref="System.Diagnostics.Metrics"/> instruments the registry and tick loop
/// record against (DR-8). The registry records job registration and removal; the
/// tick loop records fires, fire latency, missed-fire reconciliation, and dispatch
/// failures.
/// </summary>
/// <remarks>
/// <para>
/// Instruments come from the BCL <see cref="System.Diagnostics.Metrics"/>, so the
/// shipping library takes no new package dependency and stays AOT-safe. The single
/// shared instance is injected into both the <c>ScheduleRegistry</c> and the
/// <c>ScheduleTickLoop</c> so every recorded measurement flows through one meter.
/// </para>
/// <para>
/// The fire, missed-fire, and dispatch-failure instruments carry a
/// <c>job.name</c> tag; a job name is operator-controlled and bounded (it matches
/// the registry's lowercase identity pattern), so it is a safe metric dimension.
/// </para>
/// </remarks>
public sealed class SchedulerMetrics : IDisposable
{
    /// <summary>
    /// The meter name every scheduler instrument is published under. Consumers
    /// subscribe to this name to collect scheduler telemetry.
    /// </summary>
    public const string MeterName = "Bifrost.Scheduling";

    private readonly Meter meter;
    private bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SchedulerMetrics"/> class,
    /// creating the <c>Bifrost.Scheduling</c> meter and all instruments.
    /// </summary>
    public SchedulerMetrics()
    {
        this.meter = new Meter(MeterName, "1.0.0");

        this.JobsRegistered = this.meter.CreateUpDownCounter<long>(
            "bifrost.scheduling.jobs.registered",
            unit: "{job}",
            description: "Net number of registered jobs: +1 on register, -1 on unregister.");

        this.JobsFired = this.meter.CreateCounter<long>(
            "bifrost.scheduling.jobs.fired",
            unit: "{fire}",
            description: "Total successful job fires, tagged by job.name.");

        this.FireLatency = this.meter.CreateHistogram<double>(
            "bifrost.scheduling.jobs.fire_latency",
            unit: "ms",
            description: "Latency between a job's scheduled occurrence and its actual " +
                "dispatch, in milliseconds, tagged by job.name.");

        this.MissedFires = this.meter.CreateCounter<long>(
            "bifrost.scheduling.jobs.missed_fires",
            unit: "{occurrence}",
            description: "Total occurrences missed while a job could not fire, tagged by " +
                "job.name and policy.");

        this.DispatchFailures = this.meter.CreateCounter<long>(
            "bifrost.scheduling.jobs.dispatch_failures",
            unit: "{failure}",
            description: "Total job dispatch failures, tagged by job.name and exception.type.");
    }

    /// <summary>
    /// Gets the meter all scheduler instruments are created on. Exposed so a
    /// <c>MetricCollector</c> can subscribe to the exact meter instance in tests.
    /// </summary>
    /// <value>The scheduler meter.</value>
    public Meter Meter => this.meter;

    /// <summary>
    /// Gets the up-down counter tracking the net registered-job count: +1 on
    /// register, -1 on unregister.
    /// </summary>
    /// <value>The registered-jobs up-down counter.</value>
    public UpDownCounter<long> JobsRegistered { get; }

    /// <summary>
    /// Gets the counter of successful job fires, tagged by <c>job.name</c>.
    /// </summary>
    /// <value>The fired-jobs counter.</value>
    public Counter<long> JobsFired { get; }

    /// <summary>
    /// Gets the histogram of fire latency — the elapsed time between a job's
    /// scheduled occurrence and its actual dispatch — in milliseconds, tagged by
    /// <c>job.name</c>.
    /// </summary>
    /// <value>The fire-latency histogram.</value>
    public Histogram<double> FireLatency { get; }

    /// <summary>
    /// Gets the counter of missed occurrences reconciled at startup, tagged by
    /// <c>job.name</c> and <c>policy</c>.
    /// </summary>
    /// <value>The missed-fires counter.</value>
    public Counter<long> MissedFires { get; }

    /// <summary>
    /// Gets the counter of dispatch failures, tagged by <c>job.name</c> and
    /// <c>exception.type</c>.
    /// </summary>
    /// <value>The dispatch-failures counter.</value>
    public Counter<long> DispatchFailures { get; }

    /// <summary>
    /// Records a job registration: increments the live job count by one.
    /// </summary>
    public void RecordRegistered() => this.JobsRegistered.Add(1);

    /// <summary>
    /// Records a job unregistration: decrements the live job count by one.
    /// </summary>
    public void RecordUnregistered() => this.JobsRegistered.Add(-1);

    /// <summary>
    /// Records a successful fire and its latency.
    /// </summary>
    /// <param name="jobName">The job that fired.</param>
    /// <param name="latency">
    /// The elapsed time between the job's scheduled occurrence and the actual
    /// dispatch. Negative latencies (a fire dispatched at or before its occurrence)
    /// are clamped to zero so the histogram never records a negative value.
    /// </param>
    public void RecordFired(string jobName, TimeSpan latency)
    {
        var nameTag = new KeyValuePair<string, object?>("job.name", jobName);
        this.JobsFired.Add(1, nameTag);

        var millis = latency.TotalMilliseconds;
        if (millis < 0)
        {
            millis = 0;
        }

        this.FireLatency.Record(millis, nameTag);
    }

    /// <summary>
    /// Records that a job missed one or more occurrences while it could not fire.
    /// </summary>
    /// <param name="jobName">The job with missed occurrences.</param>
    /// <param name="missedCount">The number of occurrences missed.</param>
    /// <param name="policy">The policy applied to reconcile them.</param>
    public void RecordMissedFires(string jobName, int missedCount, MissedFirePolicy policy)
        => this.MissedFires.Add(
            missedCount,
            new KeyValuePair<string, object?>("job.name", jobName),
            new KeyValuePair<string, object?>("policy", policy.ToString()));

    /// <summary>
    /// Records a dispatch failure for a job.
    /// </summary>
    /// <param name="jobName">The job whose dispatch failed.</param>
    /// <param name="exceptionType">
    /// The simple type name of the exception that caused the failure, or a stable
    /// sentinel when the failure carried no exception.
    /// </param>
    public void RecordDispatchFailure(string jobName, string exceptionType)
        => this.DispatchFailures.Add(
            1,
            new KeyValuePair<string, object?>("job.name", jobName),
            new KeyValuePair<string, object?>("exception.type", exceptionType));

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.meter.Dispose();
        this.disposed = true;
    }
}
