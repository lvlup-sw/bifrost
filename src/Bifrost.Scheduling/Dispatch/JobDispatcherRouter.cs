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
    /// <remarks>
    /// The work is handed off to a pool thread, so the calling (tick) thread never awaits user code.
    /// The continuation is intentionally not awaited here, because this is fire-and-forget.
    /// <see cref="RunAsync"/> handles all failures, so the returned task never faults unobserved.
    /// </remarks>
    public void Dispatch(IJobDispatcher dispatcher, JobFireContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

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
    /// <remarks>
    /// <para>
    /// The outer <c>catch</c> isolates the dispatcher: a throwing fire must never crash the tick
    /// loop. It surfaces the failure as a failed fire that carries the exception. <c>FireTime</c> is
    /// the logical instant of the occurrence at which the dispatch was attempted.
    /// </para>
    /// <para>
    /// The publish itself is guarded. This method runs on a fire-and-forget pool task, so if the
    /// <c>Publish</c> of the sink throws (a misbehaving subscriber fan-out), the task would fault
    /// unobserved and dispatcher-failure isolation would break. The inner <c>catch</c> swallows a
    /// publish fault, so the router always isolates the dispatcher failure and the returned task
    /// never faults. A faulting sink must never break dispatcher-failure isolation, which is why
    /// CA1031 is suppressed on that <c>catch</c>. No logger is in scope for this seam (the router
    /// takes only an <see cref="ISchedulerEventSink"/>), so the publish fault is swallowed silently
    /// and not logged.
    /// </para>
    /// </remarks>
    private async Task RunAsync(IJobDispatcher dispatcher, JobFireContext context, CancellationToken ct)
    {
        try
        {
            await dispatcher.DispatchAsync(context, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                this.eventSink.Publish(new JobFireFailedEvent(
                    context.JobName,
                    context.FireTime,
                    ex));
            }
#pragma warning disable CA1031
            catch
#pragma warning restore CA1031
            {
            }
        }
    }
}
