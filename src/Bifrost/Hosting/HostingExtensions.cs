// =============================================================================
// <copyright file="HostingExtensions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.Hosting;

/// <summary>
/// Extension methods for integrating <see cref="WorkOrchestratorHostedService{TWork}"/>
/// with ASP.NET Core hosting.
/// </summary>
public static class HostingExtensions
{
    /// <summary>
    /// Adds the <see cref="WorkOrchestratorHostedService{TWork}"/> as a hosted service
    /// for lifecycle management.
    /// </summary>
    /// <typeparam name="TWork">The type of work item to process.</typeparam>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// This method registers the <see cref="WorkOrchestratorHostedService{TWork}"/> which
    /// integrates the orchestrator with the ASP.NET Core hosting lifecycle.
    /// </para>
    /// <para>
    /// Call this method after registering the orchestrator via
    /// <c>AddWorkOrchestrator&lt;TWork&gt;().Build()</c>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// services.AddWorkOrchestrator&lt;MyWork&gt;()
    ///     .WithHandler&lt;MyWorkHandler&gt;()
    ///     .Build();
    /// services.AddWorkOrchestratorHostedService&lt;MyWork&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddWorkOrchestratorHostedService<TWork>(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHostedService<WorkOrchestratorHostedService<TWork>>();
        return services;
    }
}