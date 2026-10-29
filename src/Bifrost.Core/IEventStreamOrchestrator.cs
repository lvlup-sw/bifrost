// =============================================================================
// <copyright file="IEventStreamOrchestrator.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Runtime.CompilerServices;

using Bifrost.Core.Events;

namespace Bifrost.Core;

/// <summary>
/// Extended interface for orchestrators that support event streaming.
/// </summary>
/// <typeparam name="TWork">The type of work item to process.</typeparam>
/// <remarks>
/// <para>
/// This interface extends <see cref="IWorkOrchestrator{TWork}"/> to provide
/// real-time event streaming capabilities. Events can be filtered by type
/// using the generic constraint on <see cref="GetEventStreamAsync{TEvent}"/>.
/// </para>
/// <para>
/// Multiple subscribers are supported via the broadcast pattern. Each subscriber
/// gets their own bounded buffer with drop-oldest behavior to prevent slow
/// consumers from blocking others.
/// </para>
/// <para>
/// Typical events include:
/// <list type="bullet">
///   <item><description><see cref="WorkEnqueuedEvent{TWork}"/> - when work is enqueued</description></item>
///   <item><description><see cref="WorkCompletedEvent{TWork}"/> - when work completes</description></item>
///   <item><description><see cref="ScalingEvent"/> - when workers scale up/down</description></item>
/// </list>
/// </para>
/// </remarks>
public interface IEventStreamOrchestrator<TWork> : IWorkOrchestrator<TWork>
{
    /// <summary>Reads events, optionally filtering by correlation id.</summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="correlationId">The correlation filter.</param>
    /// <param name="cancellationToken">Cancels the reader.</param>
    /// <returns>The event stream.</returns>
    IAsyncEnumerable<TEvent> GetEventStreamAsync<TEvent>(
        string? correlationId = null,
        CancellationToken cancellationToken = default)
        where TEvent : IOrchestratorEvent;
}
