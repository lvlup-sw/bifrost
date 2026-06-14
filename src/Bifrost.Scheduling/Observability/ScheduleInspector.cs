// =============================================================================
// <copyright file="ScheduleInspector.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Registry;

namespace Bifrost.Scheduling.Observability;

/// <summary>
/// The default <see cref="IBifrostScheduleInspector"/> (DR-8): a read-only view that
/// delegates job queries to the <see cref="ScheduleRegistry"/> and assembles a
/// metrics snapshot from the registry's job count and the tick health monitor's
/// recent fire count. It holds no mutating capability over the schedule.
/// </summary>
public sealed class ScheduleInspector : IBifrostScheduleInspector
{
    private readonly ScheduleRegistry registry;
    private readonly ITickHealthMonitor? fireSource;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScheduleInspector"/> class.
    /// </summary>
    /// <param name="registry">The registry whose jobs and count are inspected.</param>
    /// <param name="fireSource">
    /// The tick health monitor supplying the recent fire count for the metrics
    /// snapshot. When <see langword="null"/>, the recent fire count is reported as
    /// zero; production passes the shared monitor the tick loop updates.
    /// </param>
    internal ScheduleInspector(ScheduleRegistry registry, ITickHealthMonitor? fireSource = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        this.registry = registry;
        this.fireSource = fireSource;
    }

    /// <inheritdoc/>
    public IReadOnlyList<JobDescriptor> GetJobs() => this.registry.GetJobs();

    /// <inheritdoc/>
    public JobDescriptor? GetJob(string name) => this.registry.GetJob(name);

    /// <inheritdoc/>
    public ScheduleMetricsSnapshot GetMetricsSnapshot()
        => new(
            JobCount: this.registry.GetJobs().Count,
            RecentFireCount: this.fireSource?.RecentFireCount ?? 0);

    /// <inheritdoc/>
    public IReadOnlyList<DateTimeOffset> GetNextOccurrences(string name, int count)
        => this.registry.GetNextOccurrences(name, count);
}
