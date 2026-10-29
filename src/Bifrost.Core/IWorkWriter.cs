// =============================================================================
// <copyright file="IWorkWriter.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Core;

/// <summary>Producer-only access to a work queue. Acceptance is not a durability guarantee.</summary>
/// <typeparam name="TWork">The work item type.</typeparam>
public interface IWorkWriter<TWork>
{
    /// <summary>Attempts immediate admission and returns its outcome.</summary>
    /// <param name="work">The item to admit.</param>
    /// <param name="options">Per-item metadata.</param>
    /// <returns>The admission outcome.</returns>
    EnqueueResult Enqueue(TWork work, WorkEnqueueOptions options = default);

    /// <summary>Attempts immediate admission and returns whether it succeeded.</summary>
    /// <param name="work">The item to admit.</param>
    /// <param name="options">Per-item metadata.</param>
    /// <returns>True when admitted.</returns>
    bool TryEnqueue(TWork work, WorkEnqueueOptions options = default);

    /// <summary>Admits work, waiting for space only when the configured full mode is Wait.</summary>
    /// <param name="work">The item to admit.</param>
    /// <param name="options">Per-item metadata.</param>
    /// <param name="ct">Cancels this admission attempt; cancellation throws.</param>
    /// <returns>The admission outcome.</returns>
    ValueTask<EnqueueResult> EnqueueAsync(TWork work, WorkEnqueueOptions options = default, CancellationToken ct = default);
}
