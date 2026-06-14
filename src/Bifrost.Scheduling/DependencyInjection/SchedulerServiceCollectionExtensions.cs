// =============================================================================
// <copyright file="SchedulerServiceCollectionExtensions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Dispatch;
using Bifrost.Scheduling.Observability;
using Bifrost.Scheduling.Registry;
using Bifrost.Scheduling.Stores;
using Bifrost.Scheduling.TickEngine;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bifrost.Scheduling.DependencyInjection;

/// <summary>
/// Dependency-injection extensions that assemble the durable scheduler (DR-1/DR-5/
/// DR-6): the store, live registry, tick loop, read-only inspector, health check,
/// and the single shared set of observability components, plus any DI-time jobs.
/// </summary>
public static class SchedulerServiceCollectionExtensions
{
    /// <summary>
    /// The health check name the scheduler registers under.
    /// </summary>
    public const string HealthCheckName = "bifrost.scheduling";

    /// <summary>
    /// The expected interval between liveness ticks the health check evaluates tick
    /// staleness against. A gap exceeding three times this value is treated as a
    /// stalled loop.
    /// </summary>
    private static readonly TimeSpan ExpectedTickInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Adds the durable scheduler to the service collection: the default in-memory
    /// <see cref="IScheduleStore"/>, the live <see cref="IScheduleRegistry"/>, the
    /// tick loop as an <see cref="IHostedService"/>, the read-only inspector, the
    /// health check, and one shared <see cref="SchedulerEventStream"/>,
    /// <see cref="SchedulerMetrics"/>, and <see cref="TickHealthMonitor"/> so the
    /// registry, tick loop, and router all observe one unified timeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Ergonomics (R10):</strong> Mutating operations on <see cref="IScheduleRegistry"/>
    /// (<c>RegisterAsync</c>, <c>UpdateAsync</c>, <c>PauseAsync</c>, <c>ResumeAsync</c>)
    /// never cause immediate execution as a side effect. <c>TriggerAsync</c> is the
    /// only API that fires a job on demand.
    /// </para>
    /// <para>
    /// <strong>IIS / Azure App Service idle-timeout:</strong> On hosting environments that
    /// recycle the process after a period of inactivity (IIS idle timeout, Azure App Service
    /// "Always On" disabled), the scheduler health check provides a liveness signal. Integrate
    /// the <c>bifrost.scheduling</c> health check into the platform's liveness probe to keep
    /// the process warm — or enable "Always On" in Azure App Service to prevent recycling
    /// entirely. A recycled process loses in-memory schedule state; pair with a durable
    /// <see cref="IScheduleStore"/> (<c>UseStore&lt;T&gt;</c>) so schedules survive restart.
    /// </para>
    /// <para>
    /// Calling <c>AddScheduler</c> more than once on the same <see cref="IServiceCollection"/>
    /// throws <see cref="InvalidOperationException"/> — use the <paramref name="configure"/>
    /// callback to add additional jobs or swap the store (NCronJob#138).
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">
    /// An optional callback to add DI-time jobs and select a durable store through
    /// the fluent <see cref="ISchedulerBuilder"/>.
    /// </param>
    /// <returns>The service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// <c>AddScheduler</c> has already been called on <paramref name="services"/>.
    /// </exception>
    public static IServiceCollection AddScheduler(
        this IServiceCollection services,
        Action<ISchedulerBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Guard against double registration: calling AddScheduler twice on the same
        // IServiceCollection is a configuration error that hides conflicts and
        // produces undefined behavior (NCronJob#138, R10). The marker is a private
        // sealed class so it cannot be registered by accident from outside this method.
        if (services.Any(static sd => sd.ServiceType == typeof(SchedulerRegisteredMarker)))
        {
            throw new InvalidOperationException(
                "AddScheduler has already been called on this IServiceCollection. " +
                "Calling it more than once is not supported — use the configure callback " +
                "to add additional jobs or swap the store instead of calling AddScheduler twice.");
        }

        services.AddSingleton<SchedulerRegisteredMarker>();

        // Defaults registered with TryAdd so a caller-supplied TimeProvider is kept
        // and UseStore can replace the default store.
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IScheduleStore, InMemoryScheduleStore>();

        // The single shared observability set. The event stream is resolvable as both
        // its concrete type (for typed subscription) and ISchedulerEventSink (for the
        // publish path); the registry, tick loop, and router are all wired to it.
        services.TryAddSingleton<SchedulerEventStream>();
        services.TryAddSingleton<ISchedulerEventSink>(
            static sp => sp.GetRequiredService<SchedulerEventStream>());
        services.TryAddSingleton<SchedulerMetrics>();
        services.TryAddSingleton<TickHealthMonitor>();
        services.TryAddSingleton<ITickHealthMonitor>(
            static sp => sp.GetRequiredService<TickHealthMonitor>());

        services.TryAddSingleton<SchedulerOptions>();

        // The router carries the shared sink so a throwing dispatcher surfaces on the
        // same timeline as fires.
        services.TryAddSingleton<IJobDispatcherRouter>(
            static sp => new JobDispatcherRouter(sp.GetRequiredService<ISchedulerEventSink>()));

        // The live registry: the concrete ScheduleRegistry is resolvable both as
        // itself (the inspector reads its job snapshot) and as IScheduleRegistry,
        // passing the shared metrics and sink so its lifecycle events join the
        // unified timeline.
        services.TryAddSingleton(static sp => new ScheduleRegistry(
            sp.GetRequiredService<IScheduleStore>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<SchedulerMetrics>(),
            sp.GetRequiredService<ISchedulerEventSink>()));
        services.TryAddSingleton<IScheduleRegistry>(
            static sp => sp.GetRequiredService<ScheduleRegistry>());

        // The read-only inspection surface, reading the registry's jobs and the
        // shared health monitor's recent fire count.
        services.TryAddSingleton<IBifrostScheduleInspector>(static sp => new ScheduleInspector(
            sp.GetRequiredService<ScheduleRegistry>(),
            sp.GetRequiredService<TickHealthMonitor>()));

        // The tick loop: a single instance resolvable as itself, the hosted service,
        // and the fault source the health check reads. Construct it with the shared
        // store, time, router, sink, options, metrics, and health monitor.
        services.TryAddSingleton(static sp => new ScheduleTickLoop(
            sp.GetRequiredService<ScheduleRegistry>(),
            sp.GetRequiredService<IScheduleStore>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IJobDispatcherRouter>(),
            sp.GetRequiredService<ISchedulerEventSink>(),
            sp.GetService<ILogger<ScheduleTickLoop>>() ?? NullLogger<ScheduleTickLoop>.Instance,
            sp.GetRequiredService<SchedulerOptions>(),
            sp.GetRequiredService<SchedulerMetrics>(),
            sp.GetRequiredService<TickHealthMonitor>(),
            // Pass the root provider so each fire opens a real per-fire IServiceScope and
            // a dispatcher can resolve scoped work via JobFireContext.Services (F2/M2).
            serviceProvider: sp));
        services.TryAddSingleton<ISchedulerFaultSource>(
            static sp => sp.GetRequiredService<ScheduleTickLoop>());

        // The health check, constructed via a factory because its constructor is
        // internal and takes the expected interval. The tick loop is the fault source.
        services.AddHealthChecks()
            .Add(new HealthCheckRegistration(
                HealthCheckName,
                sp => new SchedulerHealthCheck(
                    sp.GetRequiredService<ITickHealthMonitor>(),
                    sp.GetRequiredService<ISchedulerFaultSource>(),
                    sp.GetRequiredService<TimeProvider>(),
                    ExpectedTickInterval),
                failureStatus: HealthStatus.Unhealthy,
                tags: ["scheduling"]));

        // Run the configure callback after the defaults are in place so UseStore can
        // replace the default store and AddJob/AddInlineJob accumulate definitions.
        var builder = new SchedulerBuilder(services);
        configure?.Invoke(builder);

        // Materialize the DI-time job definitions once, now that the callback has run,
        // and register the startup hosted service that wires them into the registry.
        services.AddSingleton(new SchedulerJobDefinitions(builder.Definitions));

        // Order matters: the registration service starts before the tick loop so the
        // jobs are registered before the loop dispatches. Both are added as hosted
        // services; the tick loop singleton is shared with the fault source and health
        // check registrations above.
        services.AddSingleton<IHostedService>(
            static sp => ActivatorUtilities.CreateInstance<SchedulerJobRegistrationService>(sp));
        services.AddSingleton<IHostedService>(static sp => sp.GetRequiredService<ScheduleTickLoop>());

        return services;
    }

    /// <summary>
    /// A private marker type registered as a singleton the first time
    /// <see cref="AddScheduler"/> is called. Its presence is the double-registration
    /// guard: a second call sees the descriptor and throws
    /// <see cref="InvalidOperationException"/> before any further setup runs
    /// (NCronJob#138, R10).
    /// </summary>
    private sealed class SchedulerRegisteredMarker;
}
