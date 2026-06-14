// =============================================================================
// <copyright file="JobBuilder.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Dispatch;

using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.Scheduling.DependencyInjection;

/// <summary>
/// The default <see cref="IJobBuilder{TWork}"/> (DR-1/DR-2/DR-4): accumulates a
/// job's cadence, missed-fire policy, and dispatch configuration, then materializes
/// them into a registrable <see cref="JobDefinition"/>.
/// </summary>
/// <typeparam name="TWork">The work item type an orchestrator dispatch enqueues.</typeparam>
internal sealed class JobBuilder<TWork> : IJobBuilder<TWork>, IJobDefinitionSource
{
    private readonly string name;

    private Cadence? cadence;
    private MissedFirePolicy missedFirePolicy = MissedFirePolicy.Coalesce;
    private string dispatchKind = string.Empty;
    private WorkClass workClass = WorkClass.Batch;
    private Func<IServiceProvider, IJobDispatcher>? dispatcherFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="JobBuilder{TWork}"/> class.
    /// </summary>
    /// <param name="name">The unique job name; the registry's identity key.</param>
    public JobBuilder(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        this.name = name;
    }

    /// <inheritdoc/>
    public IJobBuilder<TWork> Every(TimeSpan interval)
    {
        this.cadence = Cadence.Interval(interval);
        return this;
    }

    /// <inheritdoc/>
    public IJobBuilder<TWork> Cron(string expression, TimeZoneInfo? timeZone = null)
    {
        this.cadence = Cadence.Cron(expression, timeZone);
        return this;
    }

    /// <inheritdoc/>
    public IJobBuilder<TWork> At(DateTimeOffset fireAt)
    {
        this.cadence = Cadence.At(fireAt);
        return this;
    }

    /// <inheritdoc/>
    public IJobBuilder<TWork> After(TimeSpan delay)
    {
        this.cadence = Cadence.After(delay);
        return this;
    }

    /// <inheritdoc/>
    public IJobBuilder<TWork> WithJitter(double fraction)
    {
        this.cadence = JobBuilderCadence.ApplyJitter(this.cadence, fraction);
        return this;
    }

    /// <inheritdoc/>
    public IJobBuilder<TWork> WithMissedFirePolicy(MissedFirePolicy policy)
    {
        this.missedFirePolicy = policy;
        return this;
    }

    /// <inheritdoc/>
    public IJobBuilder<TWork> DispatchTo<TOrchestrator>(
        Func<JobFireContext, TWork> fire,
        WorkClass workClass = WorkClass.Batch)
        where TOrchestrator : IWorkOrchestrator<TWork>
    {
        ArgumentNullException.ThrowIfNull(fire);

        this.dispatchKind = JobDispatchKinds.Orchestrator;
        this.workClass = workClass;

        // Defer DI resolution to startup: resolve the orchestrator and the shared
        // sink against the application service provider when the factory runs.
        var capturedWorkClass = workClass;
        this.dispatcherFactory = provider => new OrchestratorJobDispatcher<TWork>(
            fire,
            provider.GetRequiredService<TOrchestrator>(),
            capturedWorkClass,
            provider.GetRequiredService<ISchedulerEventSink>());

        return this;
    }

    /// <inheritdoc/>
    public IJobBuilder<TWork> DispatchVia<TDispatcher>()
        where TDispatcher : class, IJobDispatcher
    {
        this.dispatchKind = JobDispatchKinds.Custom;
        this.dispatcherFactory = static provider => provider.GetRequiredService<TDispatcher>();
        return this;
    }

    /// <inheritdoc/>
    public JobDefinition Build()
    {
        var resolvedCadence = this.cadence
            ?? throw new InvalidOperationException(
                $"Job '{this.name}' has no cadence configured: call Every, Cron, At, or After.");

        var factory = this.dispatcherFactory
            ?? throw new InvalidOperationException(
                $"Job '{this.name}' has no dispatch configured: call DispatchTo or DispatchVia.");

        return new JobDefinition(
            this.name,
            resolvedCadence,
            this.missedFirePolicy,
            this.dispatchKind,
            this.workClass,
            factory);
    }
}
