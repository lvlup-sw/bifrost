// =============================================================================
// <copyright file="OrchestratorJobDispatcher.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Core.Events;

namespace Bifrost.Scheduling.Dispatch;

/// <summary>
/// The headline <see cref="IJobDispatcher"/> (DR-4): when a job fires, it builds
/// a work item from the fire context and enqueues it on a Bifrost
/// <see cref="IWorkOrchestrator{TWork}"/> under a configured
/// <see cref="WorkClass"/>, integrating scheduled jobs with Bifrost priority
/// dispatch.
/// </summary>
/// <typeparam name="TWork">The work item type the orchestrator processes.</typeparam>
/// <remarks>
/// <para>
/// Admission outcomes follow the orchestrator's contract exactly. An admission
/// <i>rejection</i> — capacity, watermark, or shutdown — is a <b>failed fire</b>,
/// not an exception: it is surfaced as a <see cref="JobFireFailedEvent"/>
/// carrying the rejection reason as text, and <see cref="DispatchAsync"/>
/// completes normally. Because rejection happens at admission, before the item
/// enters the queue, the job's next occurrence is unaffected — the tick loop
/// schedules the next fire as usual.
/// </para>
/// <para>
/// Only two things throw out of <see cref="DispatchAsync"/>: the fire function
/// throwing while building the work item, and caller-token cancellation — when
/// <see cref="IWorkWriter{TWork}.EnqueueAsync"/> surfaces an
/// <see cref="OperationCanceledException"/>, it propagates rather than being
/// folded into a failed fire, matching the orchestrator's
/// admission-vs-cancellation distinction.
/// </para>
/// <para>
/// The default work class is <see cref="WorkClass.Batch"/> so scheduled jobs are
/// throughput-oriented and never starve interactive work; callers that need a
/// scheduled job to dispatch ahead of batch work pass an explicit override.
/// </para>
/// </remarks>
public sealed class OrchestratorJobDispatcher<TWork> : IJobDispatcher
{
    private readonly Func<JobFireContext, TWork> fireFunc;
    private readonly IWorkOrchestrator<TWork> orchestrator;
    private readonly WorkClass workClass;
    private readonly ISchedulerEventSink sink;

    /// <summary>
    /// Initializes a new instance of the <see cref="OrchestratorJobDispatcher{TWork}"/>
    /// class.
    /// </summary>
    /// <param name="fireFunc">
    /// Builds the work item to enqueue from the fire context. Invoked once per
    /// fire; an exception it throws propagates out of <see cref="DispatchAsync"/>.
    /// </param>
    /// <param name="orchestrator">The orchestrator the work is enqueued on.</param>
    /// <param name="workClass">
    /// The <see cref="WorkClass"/> the work is enqueued under. Defaults to
    /// <see cref="WorkClass.Batch"/> so scheduled jobs never starve interactive
    /// work.
    /// </param>
    /// <param name="sink">
    /// The sink an admission rejection is published through as a
    /// <see cref="JobFireFailedEvent"/>. Required — it carries no default so an
    /// omitted sink is a compile error rather than a runtime
    /// <see cref="ArgumentNullException"/> (H1).
    /// </param>
    public OrchestratorJobDispatcher(
        Func<JobFireContext, TWork> fireFunc,
        IWorkOrchestrator<TWork> orchestrator,
        WorkClass workClass,
        ISchedulerEventSink sink)
    {
        ArgumentNullException.ThrowIfNull(fireFunc);
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(sink);

        this.fireFunc = fireFunc;
        this.orchestrator = orchestrator;
        this.workClass = workClass;
        this.sink = sink;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// The fire function builds the work item. If it throws, the exception propagates and the fire
    /// never reaches admission. Caller-token cancellation throws an
    /// <see cref="OperationCanceledException"/> from <c>EnqueueAsync</c>. This method intentionally
    /// does not catch it, so it propagates per the admission-vs-cancellation contract of the
    /// orchestrator.
    /// </para>
    /// <para>
    /// On acceptance there is nothing to do here, because the tick loop publishes
    /// <c>JobFiredEvent</c>. An admission rejection is a failed fire, not an exception. This method
    /// surfaces it as a <c>JobFireFailedEvent</c> that carries the rejection reason as text, and
    /// completes normally, so the next occurrence of the job is unaffected.
    /// </para>
    /// </remarks>
    public async ValueTask DispatchAsync(JobFireContext context, CancellationToken ct)
    {
        var work = this.fireFunc(context);

        var result = await this.orchestrator
            .EnqueueAsync(work, this.workClass, ct)
            .ConfigureAwait(false);

        if (result.IsAccepted)
        {
            return;
        }

        var reason = result.Reason?.ToString();
        this.sink.Publish(new JobFireFailedEvent(
            context.JobName,
            context.FireTime,
            Exception: null,
            Reason: reason));
        ScheduledDispatchOutcome.Reject(reason);
    }
}
