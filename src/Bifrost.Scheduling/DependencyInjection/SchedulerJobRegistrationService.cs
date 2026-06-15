// =============================================================================
// <copyright file="SchedulerJobRegistrationService.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;

using Microsoft.Extensions.Hosting;

namespace Bifrost.Scheduling.DependencyInjection;

/// <summary>
/// The startup hosted service that registers every DI-time job definition with the
/// live <see cref="IScheduleRegistry"/> (DR-1). It is registered as an
/// <see cref="IHostedService"/> ahead of the tick loop so the registrations are in
/// place before the loop begins dispatching.
/// </summary>
/// <remarks>
/// For each held <see cref="JobDefinition"/> it resolves the dispatcher factory
/// against the application service provider — constructing the orchestrator, inline,
/// or custom dispatcher — and calls <see cref="IScheduleRegistry.RegisterAsync"/>
/// with the definition's cadence and missed-fire policy. Even though the tick loop
/// drains pre-start registration commands during its own seeding, registering first
/// makes the ordering explicit and deterministic.
/// </remarks>
internal sealed class SchedulerJobRegistrationService : IHostedService
{
    private readonly SchedulerJobDefinitions definitions;
    private readonly IScheduleRegistry registry;
    private readonly IServiceProvider services;

    /// <summary>
    /// Initializes a new instance of the <see cref="SchedulerJobRegistrationService"/>
    /// class.
    /// </summary>
    /// <param name="definitions">The held DI-time job definitions.</param>
    /// <param name="registry">The live registry the definitions are registered with.</param>
    /// <param name="services">
    /// The application service provider each dispatcher factory is resolved against.
    /// </param>
    public SchedulerJobRegistrationService(
        SchedulerJobDefinitions definitions,
        IScheduleRegistry registry,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(services);

        this.definitions = definitions;
        this.registry = registry;
        this.services = services;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var definition in this.definitions.Definitions)
        {
            var dispatcher = definition.DispatcherFactory(this.services);
            await this.registry.RegisterAsync(
                    definition.Name,
                    definition.Cadence,
                    definition.MissedFirePolicy,
                    dispatcher,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
