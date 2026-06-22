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
/// builders and the durable-store selection during the
/// <see cref="SchedulerServiceCollectionExtensions.AddScheduler"/> callback.
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
    private Action<SchedulerOptions>? configureOptions;

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

        var builder = new JobBuilder<TWork>(name, this.services);
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

    /// <inheritdoc/>
    public ISchedulerBuilder ConfigureOptions(Action<SchedulerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        // Compose multiple calls into one delegate run in order, so a later call sees
        // (and can override) the values an earlier one set.
        var previous = this.configureOptions;
        this.configureOptions = previous is null
            ? configure
            : options =>
            {
                previous(options);
                configure(options);
            };
        return this;
    }

    /// <summary>
    /// Materializes the configured <see cref="SchedulerOptions"/>: a fresh default
    /// mutated by the accumulated <see cref="ConfigureOptions"/> callbacks, then
    /// validated. Called by <c>AddScheduler</c> after its configure callback runs so
    /// the consumer's tuning is honored.
    /// </summary>
    /// <returns>The validated options the tick loop consumes.</returns>
    /// <exception cref="InvalidOperationException">
    /// The configured options violate a <see cref="SchedulerOptions"/> constraint.
    /// </exception>
    internal SchedulerOptions BuildOptions()
    {
        var options = new SchedulerOptions();
        this.configureOptions?.Invoke(options);
        Validate(options);
        return options;
    }

    /// <summary>
    /// Fails fast on a misconfigured <see cref="SchedulerOptions"/>, naming the
    /// offending properties: each fault-recovery knob must be in range, and the
    /// cross-field constraint <c>RestartBackoff * MaxRestartsInWindow &lt; RestartWindow</c>
    /// must hold so the DR-10 give-up transition stays reachable (see
    /// <see cref="SchedulerOptions.RestartBackoff"/>).
    /// </summary>
    /// <param name="options">The configured options to validate.</param>
    /// <exception cref="InvalidOperationException">A constraint is violated.</exception>
    private static void Validate(SchedulerOptions options)
    {
        if (options.RestartBackoff < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"SchedulerOptions.{nameof(SchedulerOptions.RestartBackoff)} must be >= TimeSpan.Zero, " +
                $"but was {options.RestartBackoff}.");
        }

        if (options.MaxRestartsInWindow <= 0)
        {
            throw new InvalidOperationException(
                $"SchedulerOptions.{nameof(SchedulerOptions.MaxRestartsInWindow)} must be > 0, " +
                $"but was {options.MaxRestartsInWindow}.");
        }

        if (options.RestartWindow <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"SchedulerOptions.{nameof(SchedulerOptions.RestartWindow)} must be > TimeSpan.Zero, " +
                $"but was {options.RestartWindow}.");
        }

        // Cross-field (DR-10): a backoff that spaces consecutive faults at least as far
        // apart as the window lets each fault slide out of the sliding RestartWindow
        // before the next arrives, so the restart count never exceeds
        // MaxRestartsInWindow and the loop backs off forever instead of ever reaching
        // its give-up/faulted state. Require the spacing to stay strictly inside the
        // window. (A zero backoff disables spacing, so the constraint does not apply.)
        if (options.RestartBackoff > TimeSpan.Zero &&
            options.RestartBackoff * options.MaxRestartsInWindow >= options.RestartWindow)
        {
            throw new InvalidOperationException(
                $"SchedulerOptions fault-recovery settings are unsatisfiable: " +
                $"{nameof(SchedulerOptions.RestartBackoff)} ({options.RestartBackoff}) * " +
                $"{nameof(SchedulerOptions.MaxRestartsInWindow)} ({options.MaxRestartsInWindow}) " +
                $"must be < {nameof(SchedulerOptions.RestartWindow)} ({options.RestartWindow}). " +
                "Otherwise the backoff slides faults out of the sliding window faster than they " +
                "accumulate, so the tick loop never reaches its DR-10 give-up state and backs off " +
                $"forever. Lower {nameof(SchedulerOptions.RestartBackoff)}, lower " +
                $"{nameof(SchedulerOptions.MaxRestartsInWindow)}, or raise " +
                $"{nameof(SchedulerOptions.RestartWindow)}.");
        }
    }
}
