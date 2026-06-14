// =============================================================================
// <copyright file="InlineJobDispatcher.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;

namespace Bifrost.Scheduling.Dispatch;

/// <summary>
/// An <see cref="IJobDispatcher"/> that runs a supplied delegate when a job
/// fires, instead of enqueueing work on an orchestrator (DR-4). Use it for
/// lightweight in-process fires that do not warrant a queued work item.
/// </summary>
/// <remarks>
/// <para>
/// The delegate is always invoked on a thread-pool thread via
/// <see cref="Task.Run(Func{Task}, CancellationToken)"/>, never on the tick
/// thread: the scheduler owns timing and must not run user code on the thread
/// that drives it. An exception the delegate throws — synchronously or after an
/// await — faults the returned <see cref="ValueTask"/>, so the caller (the
/// dispatch router) observes and isolates it.
/// </para>
/// <para>
/// The <see cref="CancellationToken"/> passed to
/// <see cref="DispatchAsync"/> flows through to the delegate, so a delegate that
/// honors cancellation surfaces an <see cref="OperationCanceledException"/> that
/// propagates out of the dispatch.
/// </para>
/// <para>
/// <strong>At-least-once delivery.</strong> Execution is at-least-once per
/// scheduled occurrence — see <see cref="IJobDispatcher.DispatchAsync"/> for the
/// full contract. Make the supplied delegate idempotent and use the pair
/// (<see cref="JobFireContext.JobName"/>, <see cref="JobFireContext.FireTime"/>)
/// as the deduplication key (DR-12).
/// </para>
/// </remarks>
public sealed class InlineJobDispatcher : IJobDispatcher
{
    private readonly Func<JobFireContext, CancellationToken, ValueTask> handler;

    /// <summary>
    /// Initializes a new instance of the <see cref="InlineJobDispatcher"/> class.
    /// </summary>
    /// <param name="handler">
    /// The delegate run on each fire, receiving the fire context and the dispatch
    /// cancellation token.
    /// </param>
    public InlineJobDispatcher(Func<JobFireContext, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        this.handler = handler;
    }

    /// <inheritdoc/>
    public async ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
    {
        // Force the delegate onto a pool thread so it never runs on the tick
        // thread. Awaiting the task lets the delegate's exceptions and
        // cancellation propagate to the caller. The local handler is captured so
        // the inner lambda does not capture 'this'.
        var localHandler = this.handler;

        await Task.Run(
            async () => await localHandler(context, ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);
    }
}
