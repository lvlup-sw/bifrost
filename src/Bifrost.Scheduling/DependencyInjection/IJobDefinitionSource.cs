// =============================================================================
// <copyright file="IJobDefinitionSource.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Scheduling.DependencyInjection;

/// <summary>
/// A job builder's seam to materialize its accumulated fluent state into a
/// registrable <see cref="JobDefinition"/>. The <see cref="SchedulerBuilder"/> holds
/// the builders it created and projects each through this seam, so a definition
/// always reflects the builder's final state — including dispatch configuration set
/// after the cadence.
/// </summary>
internal interface IJobDefinitionSource
{
    /// <summary>
    /// Materializes the builder's current cadence, policy, and dispatch
    /// configuration into a registrable definition.
    /// </summary>
    /// <returns>The built job definition.</returns>
    /// <exception cref="InvalidOperationException">
    /// No cadence or no dispatch was configured.
    /// </exception>
    JobDefinition Build();
}
