// =============================================================================
// <copyright file="IDispatchableBacklog.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Queues;

/// <summary>Reports how many queued items a free worker could take now.</summary>
/// <remarks>
/// A binding implements this when some queued items can be blocked: admitted, but not
/// dispatchable until a running item releases its slot. For such a binding,
/// <see cref="IWorkQueue{T}.Count"/> is the total pending depth and
/// <see cref="DispatchableCount"/> is the part of it that more workers could run. The autoscaler
/// scales up on the dispatchable part, so a backlog that is blocked behind one saturated key does
/// not add workers.
/// </remarks>
internal interface IDispatchableBacklog
{
    /// <summary>Gets the number of queued items that a free worker could take now.</summary>
    int DispatchableCount { get; }
}
