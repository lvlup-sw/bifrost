// =============================================================================
// <copyright file="SchedulerJobRegistrationService.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Registry;

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
/// for new jobs. Existing names retain their saved cadence, policy, lifecycle, and
/// checkpoint; use explicit registry mutations to change them. Even though the tick loop
/// drains pre-start registration commands during its own seeding, registering first
/// makes the ordering explicit and deterministic.
/// </remarks>
internal sealed class SchedulerJobRegistrationService : IHostedService
{
    private readonly SchedulerJobDefinitions definitions;
    private readonly IScheduleRegistry registry;
    private readonly IServiceProvider services;
    private readonly IScheduleStore store;

    /// <summary>
    /// Initializes a new instance of the <see cref="SchedulerJobRegistrationService"/>
    /// class.
    /// </summary>
    /// <param name="definitions">The held DI-time job definitions.</param>
    /// <param name="registry">The live registry the definitions are registered with.</param>
    /// <param name="store">The saved schedule state loaded before attaching dispatchers.</param>
    /// <param name="services">
    /// The application service provider each dispatcher factory is resolved against.
    /// </param>
    public SchedulerJobRegistrationService(
        SchedulerJobDefinitions definitions,
        IScheduleRegistry registry,
        IServiceProvider services,
        IScheduleStore store)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(store);

        this.definitions = definitions;
        this.registry = registry;
        this.services = services;
        this.store = store;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Preserve durable progress and lifecycle state before new registrations can
        // overwrite it. Resolve dispatchers only from configured code, never stored type names.
        var saved = this.registry is ScheduleRegistry
            ? (await this.store.LoadAllAsync(cancellationToken).ConfigureAwait(false))
                .ToDictionary(static record => record.Name, StringComparer.Ordinal)
            : null;
        foreach (var definition in this.definitions.Definitions)
        {
            var dispatcher = definition.DispatcherFactory(this.services);
            if (this.registry is ScheduleRegistry concrete && saved!.TryGetValue(definition.Name, out var record))
            {
                await concrete.RestoreAsync(record, dispatcher, cancellationToken).ConfigureAwait(false);
                continue;
            }

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
