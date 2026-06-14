// =============================================================================
// <copyright file="SchedulerJobDefinitions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.DependencyInjection;

/// <summary>
/// A singleton holder for the DI-time job definitions accumulated by the fluent
/// builder during <see cref="SchedulerServiceCollectionExtensions.AddScheduler"/>.
/// The startup registration hosted service resolves this and registers each
/// definition with the live registry.
/// </summary>
/// <remarks>
/// The definitions are materialized once, at <c>AddScheduler</c> time after the
/// configure callback completes, so the held snapshot is final. Holding them in a
/// resolvable singleton keeps the startup hosted service free of any reference to
/// the builder itself.
/// </remarks>
internal sealed class SchedulerJobDefinitions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SchedulerJobDefinitions"/> class.
    /// </summary>
    /// <param name="definitions">The materialized DI-time job definitions.</param>
    public SchedulerJobDefinitions(IReadOnlyList<JobDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        this.Definitions = definitions;
    }

    /// <summary>
    /// Gets the DI-time job definitions registered with the scheduler at startup.
    /// </summary>
    public IReadOnlyList<JobDefinition> Definitions { get; }
}
