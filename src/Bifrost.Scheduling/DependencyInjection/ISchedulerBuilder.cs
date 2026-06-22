// =============================================================================
// <copyright file="ISchedulerBuilder.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.CodeAnalysis;

using Bifrost.Scheduling.Core;

namespace Bifrost.Scheduling.DependencyInjection;

/// <summary>
/// The fluent surface for configuring the scheduler at registration time (DR-1).
/// Returned to the <c>configure</c> callback of
/// <see cref="SchedulerServiceCollectionExtensions.AddScheduler"/>, it adds DI-time
/// jobs and selects the durable store.
/// </summary>
/// <remarks>
/// Each <c>AddJob</c>/<c>AddInlineJob</c> call returns a job builder whose cadence
/// and dispatch DSL accumulates a registrable <see cref="JobDefinition"/>. The
/// definitions are registered with the live <see cref="IScheduleRegistry"/> at host
/// startup, before the tick loop begins dispatching.
/// </remarks>
public interface ISchedulerBuilder
{
    /// <summary>
    /// Adds an orchestrator- or custom-dispatched job, returning a builder to
    /// configure its cadence and dispatch.
    /// </summary>
    /// <typeparam name="TWork">
    /// The work item type an orchestrator dispatch enqueues; also the type the
    /// fire function builds. Unused for a custom <c>DispatchVia</c> dispatch.
    /// </typeparam>
    /// <param name="name">The unique job name; the registry's identity key.</param>
    /// <returns>A job builder for the cadence and dispatch DSL.</returns>
    IJobBuilder<TWork> AddJob<TWork>(string name);

    /// <summary>
    /// Adds an inline-dispatched job that runs a supplied delegate when it fires,
    /// returning a builder to configure its cadence and the delegate.
    /// </summary>
    /// <param name="name">The unique job name; the registry's identity key.</param>
    /// <returns>An inline job builder for the cadence DSL and the <c>Run</c> delegate.</returns>
    IInlineJobBuilder AddInlineJob(string name);

    /// <summary>
    /// Tunes the scheduler's tick-loop <see cref="SchedulerOptions"/> — the shutdown
    /// grace window and the fault-recovery thresholds (restart budget, window, and
    /// backoff) the loop cannot derive from a job's cadence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The callback runs against a fresh default <see cref="SchedulerOptions"/> at
    /// <see cref="SchedulerServiceCollectionExtensions.AddScheduler"/> time, and the
    /// resulting options are validated and registered as the singleton the tick loop
    /// consumes. Without a call, the loop uses the default options unchanged.
    /// </para>
    /// <para>
    /// Calling this more than once composes the callbacks in order — a later call sees
    /// the values an earlier one set and can override them. The combined configuration
    /// is validated once, after all callbacks run; the fault-recovery cross-field
    /// constraint (<c>RestartBackoff * MaxRestartsInWindow &lt; RestartWindow</c>, see
    /// <see cref="SchedulerOptions.RestartBackoff"/>) is enforced at that point and
    /// throws if violated.
    /// </para>
    /// </remarks>
    /// <param name="configure">The callback that mutates the options.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    ISchedulerBuilder ConfigureOptions(Action<SchedulerOptions> configure);

    /// <summary>
    /// Replaces the default in-memory store with a durable
    /// <see cref="IScheduleStore"/> implementation. Calling this more than once
    /// keeps the last store specified.
    /// </summary>
    /// <typeparam name="TStore">The durable store type, resolved from DI.</typeparam>
    /// <returns>This builder, for chaining.</returns>
    ISchedulerBuilder UseStore<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStore>()
        where TStore : class, IScheduleStore;
}
