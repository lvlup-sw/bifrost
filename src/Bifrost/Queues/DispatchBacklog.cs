// =============================================================================
// <copyright file="DispatchBacklog.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Queues;

/// <summary>A consistent snapshot of a queue's pending items, split into ready and blocked items.</summary>
/// <param name="Pending">The total number of queued items. Running items are not counted.</param>
/// <param name="Ready">The number of queued items that a free worker could take now.</param>
/// <remarks>
/// <para>
/// <see cref="Pending"/> is the total pending depth that capacity and drain use. <see cref="Ready"/>
/// is the dispatchable backlog that autoscaling scale-up uses. <see cref="Blocked"/> is the rest:
/// items admitted behind a key at its concurrency limit, which run only after a running item of
/// that key releases its slot.
/// </para>
/// <para>
/// A binding without blocked items reports every pending item as ready. The counts are aggregate
/// only and never carry a work key, so they are safe to use as metric values.
/// </para>
/// </remarks>
internal readonly record struct DispatchBacklog(int Pending, int Ready)
{
    /// <summary>Gets the number of queued items that wait for a key slot.</summary>
    public int Blocked => Pending - Ready;
}
