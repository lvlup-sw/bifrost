// =============================================================================
// <copyright file="ServiceCollectionExtensions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.CodeAnalysis;
using Levelup.Channels.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Levelup.Channels.DependencyInjection;

/// <summary>
/// Extension methods for registering <see cref="IWorkOrchestrator{TWork}"/> with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds a <see cref="IWorkOrchestrator{TWork}"/> to the service collection.
    /// </summary>
    /// <typeparam name="TWork">The type of work item to process.</typeparam>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Optional configuration action for orchestrator options.</param>
    /// <returns>A builder for further configuration.</returns>
    /// <exception cref="ArgumentNullException">Thrown when services is null.</exception>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code",
        Justification = "WorkOrchestratorOptions type is fully preserved and known at compile time")]
    public static WorkOrchestratorBuilder<TWork> AddWorkOrchestrator<TWork>(
        this IServiceCollection services,
        Action<WorkOrchestratorOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Register options with DataAnnotations validation
        services.AddOptions<WorkOrchestratorOptions>()
            .Configure(configure ?? (_ => { }))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var builder = new WorkOrchestratorBuilder<TWork>(services);

        return builder;
    }
}
