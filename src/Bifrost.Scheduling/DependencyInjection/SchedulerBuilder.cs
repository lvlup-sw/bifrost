// =============================================================================
// <copyright file="SchedulerBuilder.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.CodeAnalysis;

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Stores;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bifrost.Scheduling.DependencyInjection;

/// <summary>
/// The default <see cref="ISchedulerBuilder"/> (DR-1): accumulates the DI-time job
/// builders and the durable-store selection during the <c>AddScheduler</c> callback.
/// </summary>
/// <remarks>
/// Job builders are held rather than eagerly materialized, so a definition reflects
/// the builder's final fluent state — including dispatch configuration applied after
/// the cadence. <see cref="UseStore{TStore}"/> swaps the registered
/// <see cref="IScheduleStore"/> in the service collection immediately, replacing the
/// default <see cref="InMemoryScheduleStore"/> registered by <c>AddScheduler</c>;
/// calling it again replaces the prior selection, so the last store wins.
/// </remarks>
internal sealed class SchedulerBuilder : ISchedulerBuilder
{
    private readonly IServiceCollection services;
    private readonly List<IJobDefinitionSource> jobSources = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="SchedulerBuilder"/> class.
    /// </summary>
    /// <param name="services">The service collection the builder mutates for store selection.</param>
    public SchedulerBuilder(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        this.services = services;
    }

    /// <summary>
    /// Gets the registrable definitions for every job added through this builder,
    /// materialized from each job builder's final fluent state.
    /// </summary>
    internal IReadOnlyList<JobDefinition> Definitions
        => [.. this.jobSources.Select(static source => source.Build())];

    /// <inheritdoc/>
    public IJobBuilder<TWork> AddJob<TWork>(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var builder = new JobBuilder<TWork>(name);
        this.jobSources.Add(builder);
        return builder;
    }

    /// <inheritdoc/>
    public IInlineJobBuilder AddInlineJob(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var builder = new InlineJobBuilder(name);
        this.jobSources.Add(builder);
        return builder;
    }

    /// <inheritdoc/>
    public ISchedulerBuilder UseStore<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStore>()
        where TStore : class, IScheduleStore
    {
        // Replace the store registration so the last UseStore wins: remove any prior
        // IScheduleStore descriptor (the default in-memory store or an earlier
        // selection), then register the new store as a singleton.
        this.services.RemoveAll<IScheduleStore>();
        this.services.AddSingleton<IScheduleStore, TStore>();
        return this;
    }
}
