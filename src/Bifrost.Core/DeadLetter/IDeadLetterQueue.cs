// =============================================================================
// <copyright file="IDeadLetterQueue.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Core.DeadLetter;

/// <summary>
/// Represents a dead letter queue for storing failed work items.
/// </summary>
/// <typeparam name="TWork">The type of work item.</typeparam>
/// <remarks>
/// Dead-lettered items can be inspected, reprocessed, or used for
/// diagnostics and alerting.
/// </remarks>
public interface IDeadLetterQueue<TWork>
{
    /// <summary>
    /// Enqueues a dead-lettered work item.
    /// </summary>
    /// <param name="item">The dead-lettered work item to enqueue.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
    ValueTask EnqueueAsync(DeadLetteredWork<TWork> item, CancellationToken ct = default);

    /// <summary>
    /// Reads and drains all items from the dead letter queue.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of dead-lettered work items.</returns>
    IAsyncEnumerable<DeadLetteredWork<TWork>> ReadAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the current number of items in the dead letter queue.
    /// </summary>
    /// <value>The number of dead-lettered items.</value>
    int Count { get; }
}