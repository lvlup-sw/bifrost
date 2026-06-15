// =============================================================================
// <copyright file="InlineJobBuilder.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Dispatch;

namespace Bifrost.Scheduling.DependencyInjection;

/// <summary>
/// The default <see cref="IInlineJobBuilder"/> (DR-1/DR-2/DR-4): accumulates an
/// inline job's cadence and missed-fire policy, and the delegate run on each fire,
/// then materializes a registrable <see cref="JobDefinition"/> whose factory builds
/// an <see cref="InlineJobDispatcher"/>.
/// </summary>
internal sealed class InlineJobBuilder : IInlineJobBuilder, IJobDefinitionSource
{
    private readonly string name;

    private Cadence? cadence;
    private MissedFirePolicy missedFirePolicy = MissedFirePolicy.Coalesce;
    private Func<JobFireContext, CancellationToken, ValueTask>? run;

    /// <summary>
    /// Initializes a new instance of the <see cref="InlineJobBuilder"/> class.
    /// </summary>
    /// <param name="name">The unique job name; the registry's identity key.</param>
    public InlineJobBuilder(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        this.name = name;
    }

    /// <inheritdoc/>
    public IInlineJobBuilder Every(TimeSpan interval)
    {
        this.cadence = Cadence.Interval(interval);
        return this;
    }

    /// <inheritdoc/>
    public IInlineJobBuilder Cron(string expression, TimeZoneInfo? timeZone = null)
    {
        this.cadence = Cadence.Cron(expression, timeZone);
        return this;
    }

    /// <inheritdoc/>
    public IInlineJobBuilder At(DateTimeOffset fireAt)
    {
        this.cadence = Cadence.At(fireAt);
        return this;
    }

    /// <inheritdoc/>
    public IInlineJobBuilder After(TimeSpan delay)
    {
        this.cadence = Cadence.After(delay);
        return this;
    }

    /// <inheritdoc/>
    public IInlineJobBuilder WithJitter(double fraction)
    {
        this.cadence = JobBuilderCadence.ApplyJitter(this.cadence, fraction);
        return this;
    }

    /// <inheritdoc/>
    public IInlineJobBuilder WithMissedFirePolicy(MissedFirePolicy policy)
    {
        this.missedFirePolicy = policy;
        return this;
    }

    /// <inheritdoc/>
    public IInlineJobBuilder Run(Func<JobFireContext, CancellationToken, ValueTask> run)
    {
        ArgumentNullException.ThrowIfNull(run);

        this.run = run;
        return this;
    }

    /// <inheritdoc/>
    public JobDefinition Build()
    {
        var resolvedCadence = this.cadence
            ?? throw new InvalidOperationException(
                $"Job '{this.name}' has no cadence configured: call Every, Cron, At, or After.");

        var handler = this.run
            ?? throw new InvalidOperationException(
                $"Job '{this.name}' has no inline delegate configured: call Run.");

        return new JobDefinition(
            this.name,
            resolvedCadence,
            this.missedFirePolicy,
            JobDispatchKinds.Inline,
            WorkClass.Batch,
            _ => new InlineJobDispatcher(handler));
    }
}
