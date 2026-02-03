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
/// gets their own channel with <see cref="System.Threading.Channels.BoundedChannelFullMode.DropOldest"/>
/// behavior to prevent slow consumers from blocking others.
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
    /// <summary>
    /// Gets an async enumerable of orchestrator events, filtered by type and optionally by correlation ID.
    /// </summary>
    /// <typeparam name="TEvent">The type of event to filter for.</typeparam>
    /// <param name="correlationId">
    /// Optional correlation ID to filter events. When <c>null</c>, all events of the specified type
    /// are returned. When specified, only events implementing <see cref="ICorrelatedEvent"/> with
    /// a matching correlation ID (case-sensitive, ordinal comparison) are returned.
    /// </param>
    /// <param name="cancellationToken">Cancellation token to cancel the enumeration.</param>
    /// <returns>An async enumerable of events of the specified type.</returns>
    /// <remarks>
    /// <para>
    /// The returned stream is unbounded and will continue yielding events
    /// until cancelled. Only events matching <typeparamref name="TEvent"/>
    /// will be returned.
    /// </para>
    /// <para>
    /// Each call creates a new subscriber with an independent channel. Subscribers
    /// are automatically cleaned up when the enumeration is cancelled or disposed.
    /// </para>
    /// <para>
    /// Example usage:
    /// <code>
    /// await foreach (var evt in orchestrator.GetEventStreamAsync&lt;WorkEnqueuedEvent&lt;MyWork&gt;&gt;(cancellationToken: ct))
    /// {
    ///     Console.WriteLine($"Work enqueued: {evt.Work}");
    /// }
    /// </code>
    /// </para>
    /// <para>
    /// Example with correlation ID filtering:
    /// <code>
    /// await foreach (var evt in orchestrator.GetEventStreamAsync&lt;WorkEnqueuedEvent&lt;MyWork&gt;&gt;(correlationId: "workflow-123", cancellationToken: ct))
    /// {
    ///     Console.WriteLine($"Work for workflow-123: {evt.Work}");
    /// }
    /// </code>
    /// </para>
    /// </remarks>
    IAsyncEnumerable<TEvent> GetEventStreamAsync<TEvent>(
        string? correlationId = null,
        CancellationToken cancellationToken = default)
        where TEvent : IOrchestratorEvent;

    /// <summary>
    /// Enqueues work with an optional correlation ID.
    /// </summary>
    /// <param name="work">The work item to enqueue.</param>
    /// <param name="correlationId">Optional correlation ID for the work item.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the work is enqueued.</returns>
    ValueTask EnqueueAsync(TWork work, string? correlationId, CancellationToken ct = default);

    /// <summary>
    /// Tries to enqueue work with an optional correlation ID without blocking.
    /// </summary>
    /// <param name="work">The work item to enqueue.</param>
    /// <param name="correlationId">Optional correlation ID for the work item.</param>
    /// <returns><c>true</c> if the work was enqueued; <c>false</c> if the queue was full.</returns>
    bool TryEnqueue(TWork work, string? correlationId);
}
