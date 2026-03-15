// =============================================================================
// <copyright file="IDeadLetterSubscriber.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Core.DeadLetter;

/// <summary>
/// Subscriber that reacts to dead-lettered work items.
/// </summary>
/// <typeparam name="TWork">The type of work item.</typeparam>
public interface IDeadLetterSubscriber<TWork>
{
    /// <summary>
    /// Handles a dead-lettered work item.
    /// </summary>
    /// <param name="item">The dead-lettered work item.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> that completes when the subscriber has finished handling the item.</returns>
    Task HandleAsync(DeadLetteredWork<TWork> item, CancellationToken ct);
}
