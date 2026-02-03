// =============================================================================
// <copyright file="IWorkHandler.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Core;

/// <summary>
/// Handles processing of work items from the orchestrator.
/// </summary>
/// <typeparam name="TWork">The type of work item to handle.</typeparam>
/// <remarks>
/// Implement this interface to define how work items are processed.
/// The orchestrator will invoke HandleAsync for each work item
/// from the channel.
/// </remarks>
public interface IWorkHandler<TWork>
{
    /// <summary>
    /// Handles a single work item asynchronously.
    /// </summary>
    /// <param name="work">The work item to process.</param>
    /// <param name="ct">Cancellation token to cancel the processing.</param>
    /// <returns>A ValueTask that completes when processing is done.</returns>
    /// <remarks>
    /// Implementations should handle exceptions gracefully and not throw
    /// unless the error is unrecoverable. Thrown exceptions will be
    /// reported via the orchestrator's error handling mechanism.
    /// </remarks>
    ValueTask HandleAsync(TWork work, CancellationToken ct);
}
