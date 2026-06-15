// =============================================================================
// <copyright file="IInlineJobBuilder.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;

namespace Bifrost.Scheduling.DependencyInjection;

/// <summary>
/// The fluent cadence DSL and inline-dispatch terminal for a job that runs a
/// supplied delegate when it fires (DR-1/DR-2/DR-4), rather than enqueueing work on
/// an orchestrator.
/// </summary>
public interface IInlineJobBuilder
{
    /// <summary>
    /// Schedules the job on a fixed interval (<see cref="IntervalCadence"/>).
    /// </summary>
    /// <param name="interval">The spacing between occurrences; must be positive.</param>
    /// <returns>This builder, for chaining.</returns>
    IInlineJobBuilder Every(TimeSpan interval);

    /// <summary>
    /// Schedules the job from a cron expression (<see cref="CronCadence"/>).
    /// </summary>
    /// <param name="expression">The standard five-field cron expression.</param>
    /// <param name="timeZone">
    /// The time zone the expression is evaluated in, or <see langword="null"/> to
    /// evaluate in UTC.
    /// </param>
    /// <returns>This builder, for chaining.</returns>
    IInlineJobBuilder Cron(string expression, TimeZoneInfo? timeZone = null);

    /// <summary>
    /// Schedules the job to fire once at an absolute instant
    /// (<see cref="OneShotCadence"/>).
    /// </summary>
    /// <param name="fireAt">The instant the job fires.</param>
    /// <returns>This builder, for chaining.</returns>
    IInlineJobBuilder At(DateTimeOffset fireAt);

    /// <summary>
    /// Schedules the job to fire once after a relative delay
    /// (<see cref="RelativeOneShotCadence"/>). The registry resolves the delay to an
    /// absolute instant against the scheduler clock at registration time.
    /// </summary>
    /// <param name="delay">The delay before the single fire.</param>
    /// <returns>This builder, for chaining.</returns>
    IInlineJobBuilder After(TimeSpan delay);

    /// <summary>
    /// Applies a jitter fraction to an interval cadence to spread load.
    /// </summary>
    /// <param name="fraction">The jitter fraction in <c>[0, 1]</c>.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// The current cadence is not an <see cref="IntervalCadence"/>.
    /// </exception>
    IInlineJobBuilder WithJitter(double fraction);

    /// <summary>
    /// Sets the missed-fire policy the scheduler applies to occurrences missed while
    /// the job could not fire.
    /// </summary>
    /// <param name="policy">The missed-fire policy.</param>
    /// <returns>This builder, for chaining.</returns>
    IInlineJobBuilder WithMissedFirePolicy(MissedFirePolicy policy);

    /// <summary>
    /// Dispatches each fire by running the supplied delegate on a pool thread
    /// (<see cref="Dispatch.InlineJobDispatcher"/>).
    /// </summary>
    /// <param name="run">
    /// The delegate run on each fire, receiving the fire context and the dispatch
    /// cancellation token.
    /// </param>
    /// <returns>This builder, for chaining.</returns>
    IInlineJobBuilder Run(Func<JobFireContext, CancellationToken, ValueTask> run);
}
