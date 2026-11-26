// =============================================================================
// <copyright file="DispatchableBacklogSource.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Queues;

namespace Bifrost.Autoscaling;

/// <summary>
/// Carries the dispatchable-backlog signal of a keyed orchestrator from the builder to the
/// autoscaling coordinator.
/// </summary>
/// <typeparam name="TWork">The work item type.</typeparam>
/// <remarks>
/// The builder registers this only for keyed dispatch and attaches the base
/// <see cref="WorkOrchestrator{TWork}"/> when it creates it, before any decorator wraps it. The
/// coordinator factory resolves the orchestrator first, so the signal is set when it is read.
/// </remarks>
internal sealed class DispatchableBacklogSource<TWork>
{
    /// <summary>Gets the signal, or <see langword="null"/> before an orchestrator is attached.</summary>
    public Func<int>? DispatchableCount { get; private set; }

    /// <summary>
    /// Gets the pending, ready, and blocked counts of the attached orchestrator, or
    /// <see langword="null"/> before an orchestrator is attached.
    /// </summary>
    /// <remarks>
    /// Health checks and gauges read this instead of the decorated orchestrator, which only
    /// exposes the total pending count. The counts are aggregate and carry no work key.
    /// </remarks>
    public Func<DispatchBacklog>? Backlog { get; private set; }

    /// <summary>Attaches the base orchestrator whose dispatchable backlog the signal reads.</summary>
    /// <param name="orchestrator">The undecorated orchestrator.</param>
    public void Attach(WorkOrchestrator<TWork> orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        DispatchableCount = () => orchestrator.DispatchableCount;
        Backlog = () => orchestrator.Backlog;
    }
}
