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
    /// Replaces the default in-memory store with a durable
    /// <see cref="IScheduleStore"/> implementation. Calling this more than once
    /// keeps the last store specified.
    /// </summary>
    /// <typeparam name="TStore">The durable store type, resolved from DI.</typeparam>
    /// <returns>This builder, for chaining.</returns>
    ISchedulerBuilder UseStore<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStore>()
        where TStore : class, IScheduleStore;
}
