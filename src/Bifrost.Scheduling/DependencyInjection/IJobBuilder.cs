// =============================================================================
// <copyright file="IJobBuilder.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Scheduling.Core;

namespace Bifrost.Scheduling.DependencyInjection;

/// <summary>
/// The fluent cadence and dispatch DSL for an orchestrator- or custom-dispatched
/// job (DR-1/DR-2/DR-4). The cadence methods select and tune the schedule; the
/// dispatch methods select how a fire turns into work.
/// </summary>
/// <typeparam name="TWork">
/// The work item type an orchestrator dispatch enqueues and the fire function
/// builds. Unused for a custom <see cref="DispatchVia{TDispatcher}"/> dispatch.
/// </typeparam>
public interface IJobBuilder<TWork>
{
    /// <summary>
    /// Schedules the job on a fixed interval (<see cref="IntervalCadence"/>).
    /// </summary>
    /// <param name="interval">The spacing between occurrences; must be positive.</param>
    /// <returns>This builder, for chaining.</returns>
    IJobBuilder<TWork> Every(TimeSpan interval);

    /// <summary>
    /// Schedules the job from a cron expression (<see cref="CronCadence"/>).
    /// </summary>
    /// <param name="expression">The standard five-field cron expression.</param>
    /// <param name="timeZone">
    /// The time zone the expression is evaluated in, or <see langword="null"/> to
    /// evaluate in UTC.
    /// </param>
    /// <returns>This builder, for chaining.</returns>
    IJobBuilder<TWork> Cron(string expression, TimeZoneInfo? timeZone = null);

    /// <summary>
    /// Schedules the job to fire once at an absolute instant
    /// (<see cref="OneShotCadence"/>).
    /// </summary>
    /// <param name="fireAt">The instant the job fires.</param>
    /// <returns>This builder, for chaining.</returns>
    IJobBuilder<TWork> At(DateTimeOffset fireAt);

    /// <summary>
    /// Schedules the job to fire once after a relative delay
    /// (<see cref="RelativeOneShotCadence"/>). The registry resolves the delay to an
    /// absolute instant against the scheduler clock at registration time.
    /// </summary>
    /// <param name="delay">The delay before the single fire.</param>
    /// <returns>This builder, for chaining.</returns>
    IJobBuilder<TWork> After(TimeSpan delay);

    /// <summary>
    /// Applies a jitter fraction to an interval cadence to spread load.
    /// </summary>
    /// <param name="fraction">The jitter fraction in <c>[0, 1]</c>.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// The current cadence is not an <see cref="IntervalCadence"/>.
    /// </exception>
    IJobBuilder<TWork> WithJitter(double fraction);

    /// <summary>
    /// Sets the missed-fire policy the scheduler applies to occurrences missed while
    /// the job could not fire.
    /// </summary>
    /// <param name="policy">The missed-fire policy.</param>
    /// <returns>This builder, for chaining.</returns>
    IJobBuilder<TWork> WithMissedFirePolicy(MissedFirePolicy policy);

    /// <summary>
    /// Dispatches each fire by building a work item with <paramref name="fire"/> and
    /// enqueueing it on the resolved <typeparamref name="TOrchestrator"/> under the
    /// supplied <paramref name="workClass"/>.
    /// </summary>
    /// <typeparam name="TOrchestrator">
    /// The orchestrator service type resolved from DI; must be an
    /// <see cref="IWorkOrchestrator{TWork}"/> over the same <typeparamref name="TWork"/>.
    /// </typeparam>
    /// <param name="fire">Builds the work item to enqueue from the fire context.</param>
    /// <param name="workClass">
    /// The <see cref="WorkClass"/> the work is enqueued under. Defaults to
    /// <see cref="WorkClass.Batch"/> (post-CPQ #16-Q6) so scheduled jobs never
    /// starve interactive work; pass an override to dispatch ahead of batch work.
    /// </param>
    /// <returns>This builder, for chaining.</returns>
    IJobBuilder<TWork> DispatchTo<TOrchestrator>(
        Func<JobFireContext, TWork> fire,
        WorkClass workClass = WorkClass.Batch)
        where TOrchestrator : IWorkOrchestrator<TWork>;

    /// <summary>
    /// Dispatches each fire through a custom <see cref="IJobDispatcher"/> resolved
    /// from DI.
    /// </summary>
    /// <typeparam name="TDispatcher">The custom dispatcher type, resolved from DI.</typeparam>
    /// <returns>This builder, for chaining.</returns>
    IJobBuilder<TWork> DispatchVia<TDispatcher>()
        where TDispatcher : class, IJobDispatcher;
}
