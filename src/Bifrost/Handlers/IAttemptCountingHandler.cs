// =============================================================================
// <copyright file="IAttemptCountingHandler.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

namespace Bifrost.Handlers;

/// <summary>
/// A handler that can run more than one handler attempt per call and reports how many it ran.
/// </summary>
/// <typeparam name="TWork">The type of work item to handle.</typeparam>
/// <remarks>
/// <para>
/// Handler resilience (<c>WithHandlerResilience</c>) implements this interface. It is registered in
/// the reserved handler decorator band, so it is always the outermost handler decorator and the
/// orchestrator finds it as its own handler. The orchestrator then calls
/// <see cref="HandleAsync(TWork, HandlerAttemptCounter, CancellationToken)"/> and adds the reported
/// attempts to the item's terminal outcome, so attempt counts and attempt metrics include the
/// retries that run inside the handler.
/// </para>
/// <para>
/// The implementation must add one to the counter at the start of each attempt, before the inner
/// handler runs, so a failed or canceled attempt is counted too.
/// </para>
/// </remarks>
internal interface IAttemptCountingHandler<TWork> : IWorkHandler<TWork>
{
    /// <summary>Handles one work item and counts each handler attempt in <paramref name="attempts"/>.</summary>
    /// <param name="work">The work item.</param>
    /// <param name="attempts">The counter that receives one increment per attempt.</param>
    /// <param name="ct">The handler token from the orchestrator.</param>
    /// <returns>A task that completes when the last attempt exits.</returns>
    ValueTask HandleAsync(TWork work, HandlerAttemptCounter attempts, CancellationToken ct);
}

/// <summary>A mutable count of the handler attempts one work item used.</summary>
/// <remarks>
/// The orchestrator creates one counter per item, and only when its handler is an
/// <see cref="IAttemptCountingHandler{TWork}"/>. One worker uses the counter at a time, but the
/// increment is atomic so a handler may count from any thread.
/// </remarks>
internal sealed class HandlerAttemptCounter
{
    private int _count;

    /// <summary>Gets the number of attempts counted so far.</summary>
    internal int Count => Volatile.Read(ref _count);

    /// <summary>Counts one more attempt.</summary>
    internal void Increment() => Interlocked.Increment(ref _count);
}
