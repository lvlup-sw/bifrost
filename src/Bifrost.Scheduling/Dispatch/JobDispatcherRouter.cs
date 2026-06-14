// =============================================================================
// <copyright file="JobDispatcherRouter.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Core.Events;

namespace Bifrost.Scheduling.Dispatch;

/// <summary>
/// The default <see cref="IJobDispatcherRouter"/>: hands each fire off to a
/// thread-pool thread, invokes the job's <see cref="IJobDispatcher"/>, and
/// isolates a throwing dispatcher as a <see cref="JobFireFailedEvent"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Dispatch"/> is fire-and-forget: it schedules the dispatch on the
/// pool with <see cref="Task.Run(Func{Task}, CancellationToken)"/> and returns
/// immediately, so the tick thread that calls it never awaits user code. Any
/// exception thrown by <see cref="IJobDispatcher.DispatchAsync"/> — including
/// one thrown synchronously before the first await — is caught on the pool
/// thread and published as a <see cref="JobFireFailedEvent"/> carrying the
/// exception, never propagated back to the caller.
/// </para>
/// <para>
/// An <see cref="OperationCanceledException"/> from a cancelled dispatch is
/// treated like any other failure here: the fire did not complete, so it is
/// surfaced as a failed fire rather than swallowed silently. The tick loop, not
/// the router, decides when to cancel a dispatch.
/// </para>
/// </remarks>
internal sealed class JobDispatcherRouter : IJobDispatcherRouter
{
    private readonly ISchedulerEventSink eventSink;

    /// <summary>
    /// Initializes a new instance of the <see cref="JobDispatcherRouter"/> class.
    /// </summary>
    /// <param name="eventSink">
    /// The sink a failed fire is published through, so a throwing dispatcher
    /// surfaces as a <see cref="JobFireFailedEvent"/> instead of crashing the
    /// caller.
    /// </param>
    public JobDispatcherRouter(ISchedulerEventSink eventSink)
    {
        ArgumentNullException.ThrowIfNull(eventSink);

        this.eventSink = eventSink;
    }

    /// <inheritdoc/>
    public void Dispatch(IJobDispatcher dispatcher, JobFireContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        // Hand off to a pool thread so the calling (tick) thread never awaits
        // user code. The continuation is intentionally not awaited here — this
        // is fire-and-forget — and all failures are handled inside RunAsync, so
        // the returned task never faults unobserved.
        _ = Task.Run(() => this.RunAsync(dispatcher, context, ct), CancellationToken.None);
    }

    /// <summary>
    /// Invokes the dispatcher on the pool thread and converts any failure into a
    /// <see cref="JobFireFailedEvent"/>.
    /// </summary>
    /// <param name="dispatcher">The job's dispatcher.</param>
    /// <param name="context">The fire context.</param>
    /// <param name="ct">The dispatch cancellation token.</param>
    /// <returns>A task that always completes successfully; failures are published.</returns>
    private async Task RunAsync(IJobDispatcher dispatcher, JobFireContext context, CancellationToken ct)
    {
        try
        {
            await dispatcher.DispatchAsync(context, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Isolate the dispatcher: a throwing fire must never crash the tick
            // loop. Surface it as a failed fire carrying the exception. FireTime
            // is the occurrence's logical instant the dispatch was attempted at.
            this.eventSink.Publish(new JobFireFailedEvent(
                context.JobName,
                context.FireTime,
                ex));
        }
    }
}
