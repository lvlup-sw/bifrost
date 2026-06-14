// =============================================================================
// <copyright file="AddSchedulerTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.DependencyInjection;
using Bifrost.Scheduling.Observability;
using Bifrost.Scheduling.Registry;
using Bifrost.Scheduling.Stores;
using Bifrost.Scheduling.TickEngine;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.DependencyInjection;

/// <summary>
/// Tests for <see cref="SchedulerServiceCollectionExtensions.AddScheduler"/> and
/// <see cref="ISchedulerBuilder.UseStore{TStore}"/> (Task 39, DR-1/DR-5/DR-6): the
/// extension wires the durable store, registry, tick loop, health check, time
/// provider, and the shared observability singletons; DI-time jobs are registered at
/// startup; and the store selection is overridable and last-wins.
/// </summary>
public sealed class AddSchedulerTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 14, 0, 0, 0, TimeSpan.Zero);

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies <c>AddScheduler</c> registers the default in-memory store.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AddScheduler_RegistersDefaultInMemoryStore()
    {
        var provider = new ServiceCollection().AddScheduler().BuildServiceProvider();

        var store = provider.GetRequiredService<IScheduleStore>();

        await Assert.That(store).IsTypeOf<InMemoryScheduleStore>();
    }

    /// <summary>
    /// Verifies <c>AddScheduler</c> registers the <see cref="ScheduleRegistry"/> as
    /// the <see cref="IScheduleRegistry"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AddScheduler_RegistersScheduleRegistry()
    {
        var provider = new ServiceCollection().AddScheduler().BuildServiceProvider();

        var registry = provider.GetRequiredService<IScheduleRegistry>();

        await Assert.That(registry).IsTypeOf<ScheduleRegistry>();
    }

    /// <summary>
    /// Verifies the tick loop is registered as an <see cref="IHostedService"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AddScheduler_RegistersTickLoopAsHostedService()
    {
        var provider = NewServices().AddScheduler().BuildServiceProvider();

        var hostedServices = provider.GetServices<IHostedService>();

        await Assert.That(hostedServices.OfType<ScheduleTickLoop>().Any()).IsTrue();
    }

    /// <summary>
    /// Verifies the scheduler health check is registered under its name.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AddScheduler_RegistersHealthCheck()
    {
        var provider = new ServiceCollection().AddScheduler().BuildServiceProvider();

        var options = provider.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>().Value;

        await Assert.That(options.Registrations.Any(r => r.Name == "bifrost.scheduling")).IsTrue();
    }

    /// <summary>
    /// Verifies <c>AddScheduler</c> registers <see cref="TimeProvider.System"/> when
    /// none is already present.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AddScheduler_RegistersSystemTimeProvider_WhenAbsent()
    {
        var provider = new ServiceCollection().AddScheduler().BuildServiceProvider();

        var timeProvider = provider.GetRequiredService<TimeProvider>();

        await Assert.That(timeProvider).IsSameReferenceAs(TimeProvider.System);
    }

    /// <summary>
    /// Verifies a pre-registered <see cref="TimeProvider"/> is preserved (the
    /// default is added with TryAdd, so an existing registration wins).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AddScheduler_PreservesExistingTimeProvider()
    {
        var fake = new FakeTimeProvider(Start);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(fake);

        var provider = services.AddScheduler().BuildServiceProvider();
        var timeProvider = provider.GetRequiredService<TimeProvider>();

        await Assert.That(timeProvider).IsSameReferenceAs((TimeProvider)fake);
    }

    /// <summary>
    /// Verifies the shared observability singletons are wired so a subscriber on the
    /// resolved <see cref="SchedulerEventStream"/> observes events the registry and
    /// tick loop publish (one unified timeline). The same instance is resolvable as
    /// both <see cref="SchedulerEventStream"/> and <see cref="ISchedulerEventSink"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AddScheduler_WiresSharedEventStream()
    {
        var provider = new ServiceCollection().AddScheduler().BuildServiceProvider();

        var stream = provider.GetRequiredService<SchedulerEventStream>();
        var sink = provider.GetRequiredService<ISchedulerEventSink>();

        await Assert.That(sink).IsSameReferenceAs((ISchedulerEventSink)stream);
    }

    /// <summary>
    /// Verifies the read-only inspector is registered.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AddScheduler_RegistersInspector()
    {
        var provider = new ServiceCollection().AddScheduler().BuildServiceProvider();

        var inspector = provider.GetRequiredService<IBifrostScheduleInspector>();

        await Assert.That(inspector).IsNotNull();
    }

    /// <summary>
    /// Verifies a DI-time job configured through the builder is present in the
    /// registry once the startup hosted services have run.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AddScheduler_DiTimeJob_IsRegisteredAtStartup()
    {
        var services = NewServices();
        services.AddScheduler(scheduler =>
            scheduler.AddInlineJob("startup-job")
                .Every(TimeSpan.FromMinutes(5))
                .Run((_, _) => ValueTask.CompletedTask));
        await using var provider = services.BuildServiceProvider();

        await StartHostedServicesAsync(provider).ConfigureAwait(false);
        try
        {
            var registry = provider.GetRequiredService<IScheduleRegistry>();
            var job = registry.GetJob("startup-job");

            await Assert.That(job).IsNotNull();
            await Assert.That(job!.Value.Name).IsEqualTo("startup-job");
        }
        finally
        {
            await StopHostedServicesAsync(provider).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies a DI-time inline job actually fires once its cadence elapses, proving
    /// the registration hosted service runs before the tick loop dispatches.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AddScheduler_DiTimeJob_FiresAtStartup()
    {
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new FakeTimeProvider(Start);

        var services = NewServices();
        services.AddSingleton<TimeProvider>(time);
        services.AddScheduler(scheduler =>
            scheduler.AddInlineJob("fires")
                .Every(TimeSpan.FromMinutes(5))
                .Run((_, _) =>
                {
                    fired.TrySetResult();
                    return ValueTask.CompletedTask;
                }));
        await using var provider = services.BuildServiceProvider();

        await StartHostedServicesAsync(provider).ConfigureAwait(false);
        try
        {
            var loop = provider.GetServices<IHostedService>().OfType<ScheduleTickLoop>().Single();
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            time.Advance(TimeSpan.FromMinutes(5));
            await loop.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            var completed = await Task.WhenAny(fired.Task, Task.Delay(TestTimeout)).ConfigureAwait(false);
            await Assert.That(completed == fired.Task).IsTrue();
        }
        finally
        {
            await StopHostedServicesAsync(provider).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies <see cref="ISchedulerBuilder.UseStore{TStore}"/> replaces the default
    /// in-memory store with the supplied store.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task UseStore_ReplacesInMemoryStore()
    {
        var services = new ServiceCollection();
        services.AddSingleton<CustomStore>();
        services.AddScheduler(scheduler => scheduler.UseStore<CustomStore>());
        var provider = services.BuildServiceProvider();

        var store = provider.GetRequiredService<IScheduleStore>();

        await Assert.That(store).IsTypeOf<CustomStore>();
    }

    /// <summary>
    /// Verifies the last <see cref="ISchedulerBuilder.UseStore{TStore}"/> call wins
    /// when called more than once.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task UseStore_CalledTwice_LastWins()
    {
        var services = new ServiceCollection();
        services.AddSingleton<CustomStore>();
        services.AddSingleton<OtherStore>();
        services.AddScheduler(scheduler =>
        {
            scheduler.UseStore<CustomStore>();
            scheduler.UseStore<OtherStore>();
        });
        var provider = services.BuildServiceProvider();

        var store = provider.GetRequiredService<IScheduleStore>();

        await Assert.That(store).IsTypeOf<OtherStore>();
    }

    /// <summary>
    /// Verifies only one <see cref="IScheduleStore"/> is registered after
    /// <c>UseStore</c> replaces the default (no leftover in-memory descriptor).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task UseStore_ReplacesRatherThanAppends()
    {
        var services = new ServiceCollection();
        services.AddSingleton<CustomStore>();
        services.AddScheduler(scheduler => scheduler.UseStore<CustomStore>());
        var provider = services.BuildServiceProvider();

        var stores = provider.GetServices<IScheduleStore>();

        await Assert.That(stores).HasCount(1);
        await Assert.That(stores.Single()).IsTypeOf<CustomStore>();
    }

    /// <summary>
    /// Builds a service collection with logging registered — the baseline host
    /// service the framework health-check hosted service depends on. Tests that
    /// resolve <see cref="IHostedService"/> need it; tests that only assert single
    /// registrations do not, exercising that <c>AddScheduler</c> itself does not
    /// require the caller to pre-register logging.
    /// </summary>
    /// <returns>A service collection with logging registered.</returns>
    private static ServiceCollection NewServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return services;
    }

    private static async Task StartHostedServicesAsync(IServiceProvider provider)
    {
        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            await hosted.StartAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task StopHostedServicesAsync(IServiceProvider provider)
    {
        foreach (var hosted in provider.GetServices<IHostedService>().Reverse())
        {
            await hosted.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed class CustomStore : IScheduleStore
    {
        public ValueTask<IReadOnlyList<JobRecord>> LoadAllAsync(CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<JobRecord>>([]);

        public ValueTask SaveAsync(JobRecord job, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask RecordFiredAsync(
            string jobName,
            DateTimeOffset firedAt,
            DateTimeOffset? nextFireAt,
            CancellationToken ct)
            => ValueTask.CompletedTask;

        public ValueTask DeleteAsync(string jobName, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class OtherStore : IScheduleStore
    {
        public ValueTask<IReadOnlyList<JobRecord>> LoadAllAsync(CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<JobRecord>>([]);

        public ValueTask SaveAsync(JobRecord job, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask RecordFiredAsync(
            string jobName,
            DateTimeOffset firedAt,
            DateTimeOffset? nextFireAt,
            CancellationToken ct)
            => ValueTask.CompletedTask;

        public ValueTask DeleteAsync(string jobName, CancellationToken ct) => ValueTask.CompletedTask;
    }
}
