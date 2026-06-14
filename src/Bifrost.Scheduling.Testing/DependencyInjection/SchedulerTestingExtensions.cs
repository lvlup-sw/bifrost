// =============================================================================
// <copyright file="SchedulerTestingExtensions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.TickEngine;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Scheduling.Testing.DependencyInjection;

/// <summary>
/// Dependency-injection extensions that wire the scheduler's testing primitives
/// into the service collection (DR-9): a <see cref="FakeTimeProvider"/> clock and
/// the <see cref="ISchedulerTestHarness"/> control surface.
/// </summary>
public static class SchedulerTestingExtensions
{
    /// <summary>
    /// Adds the scheduler testing infrastructure to <paramref name="services"/>:
    /// replaces any registered <see cref="TimeProvider"/> with a
    /// <see cref="FakeTimeProvider"/> (last-wins, like <c>UseStore&lt;T&gt;</c>), and
    /// registers <see cref="ISchedulerTestHarness"/> as a singleton backed by the
    /// <see cref="SchedulerTestHarness"/> implementation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this method <em>after</em> <c>AddScheduler</c> so the fake provider
    /// replaces the default <see cref="TimeProvider.System"/> that
    /// <c>AddScheduler</c> registers with <c>TryAdd</c>. Calling it before
    /// <c>AddScheduler</c> also works because the replacement is unconditional
    /// (<c>RemoveAll</c> + <c>AddSingleton</c>), so the last call to either method
    /// wins.
    /// </para>
    /// <para>
    /// The resolved <see cref="ISchedulerTestHarness"/> is usable only once the
    /// tick loop's <see cref="Microsoft.Extensions.Hosting.IHostedService.StartAsync"/>
    /// has been called (i.e., inside a started host). Resolving it before that is
    /// safe — it will be constructed lazily — but calling its methods before the
    /// loop is started has no effect.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    public static IServiceCollection AddSchedulerTesting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Replace any existing TimeProvider registration with FakeTimeProvider.
        // This mirrors the UseStore<T> pattern: RemoveAll clears any prior
        // descriptor (the default TimeProvider.System or a caller-supplied one),
        // then AddSingleton registers the fake. Because FakeTimeProvider derives
        // from TimeProvider, the concrete type is resolvable as both.
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<FakeTimeProvider>();
        services.AddSingleton<TimeProvider>(static sp => sp.GetRequiredService<FakeTimeProvider>());

        // Register the harness: it wraps the tick loop singleton (already in DI
        // from AddScheduler) with the fake clock so tests can advance time and
        // wait for idle without real wall-clock delays.
        services.TryAddSingleton<ISchedulerTestHarness>(static sp =>
            new SchedulerTestHarness(
                sp.GetRequiredService<ScheduleTickLoop>(),
                sp.GetRequiredService<FakeTimeProvider>()));

        return services;
    }
}
