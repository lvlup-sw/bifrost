// =============================================================================
// <copyright file="WorkOrchestratorBuilder.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bifrost.DependencyInjection;

/// <summary>
/// Builder for configuring <see cref="IWorkOrchestrator{TWork}"/> with optional decorators.
/// </summary>
/// <typeparam name="TWork">The type of work item to process.</typeparam>
public sealed class WorkOrchestratorBuilder<TWork>
{
    /// <summary>
    /// Gets the service collection for registering additional services.
    /// </summary>
    /// <value>The service collection.</value>
    internal IServiceCollection Services { get; }

    /// <summary>
    /// Gets the list of decorator registrations.
    /// </summary>
    internal List<DecoratorRegistration<TWork>> Decorators { get; } = [];

    /// <summary>
    /// Gets the list of handler decorator factories applied before the handler is passed to the orchestrator.
    /// </summary>
    /// <value>A list of factory functions that wrap the handler.</value>
    internal List<Func<IServiceProvider, IWorkHandler<TWork>, IWorkHandler<TWork>>> HandlerDecorators { get; } = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkOrchestratorBuilder{TWork}"/> class.
    /// </summary>
    /// <param name="services">The service collection to register services with.</param>
    internal WorkOrchestratorBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>
    /// Builds and registers the <see cref="IWorkOrchestrator{TWork}"/> with all configured decorators.
    /// </summary>
    public void Build()
    {
        // Sort decorators by order (innermost first)
        var orderedDecorators = Decorators.OrderBy(d => d.Order).ToList();

        // Capture handler decorators for closure
        var handlerDecorators = HandlerDecorators.ToList();

        Services.AddSingleton<IWorkOrchestrator<TWork>>(sp =>
        {
            // Resolve handler from DI and apply handler decorators in order
            IWorkHandler<TWork> handler = sp.GetRequiredService<IWorkHandler<TWork>>();

            foreach (var decorator in handlerDecorators)
            {
                handler = decorator(sp, handler);
            }

            // Start with base implementation using the decorated handler
            IWorkOrchestrator<TWork> orchestrator = new WorkOrchestrator<TWork>(
                handler,
                sp.GetRequiredService<IOptions<WorkOrchestratorOptions>>(),
                sp.GetRequiredService<ILogger<WorkOrchestrator<TWork>>>());

            // Apply orchestrator decorators in order
            foreach (var registration in orderedDecorators)
            {
                orchestrator = registration.Factory(sp, orchestrator);
            }

            return orchestrator;
        });
    }
}

/// <summary>
/// Registration for a decorator in the chain.
/// </summary>
/// <typeparam name="TWork">The type of work item to process.</typeparam>
/// <param name="Order">The order in which to apply this decorator (lower = innermost).</param>
/// <param name="Factory">Factory function to create the decorator.</param>
internal sealed record DecoratorRegistration<TWork>(
    int Order,
    Func<IServiceProvider, IWorkOrchestrator<TWork>, IWorkOrchestrator<TWork>> Factory);