// =============================================================================
// <copyright file="SchedulerOptionsConfigurationTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.Tests.Scheduling.DependencyInjection;

/// <summary>
/// Tests for <see cref="ISchedulerBuilder.ConfigureOptions"/> and the fault-recovery
/// validation it enables: the configured <see cref="SchedulerOptions"/> are resolvable
/// from the provider, defaults are preserved when the consumer does not configure, the
/// fluent surface composes with the other builder methods, and an unsatisfiable
/// fault-recovery configuration fails fast at <c>AddScheduler</c> time (the MEDIUM-1
/// cross-field constraint, see <see cref="SchedulerOptions.RestartBackoff"/>).
/// </summary>
public sealed class SchedulerOptionsConfigurationTests
{
    /// <summary>
    /// Verifies values set through <see cref="ISchedulerBuilder.ConfigureOptions"/> are
    /// reflected in the <see cref="SchedulerOptions"/> resolved from the provider.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ConfigureOptions_SetsValues_ResolvedOptionsReflectThem()
    {
        var provider = new ServiceCollection()
            .AddScheduler(builder => builder.ConfigureOptions(options =>
            {
                options.RestartBackoff = TimeSpan.FromSeconds(2);
                options.MaxRestartsInWindow = 5;
            }))
            .BuildServiceProvider();

        var options = provider.GetRequiredService<SchedulerOptions>();

        await Assert.That(options.RestartBackoff).IsEqualTo(TimeSpan.FromSeconds(2));
        await Assert.That(options.MaxRestartsInWindow).IsEqualTo(5);
    }

    /// <summary>
    /// Verifies that without a <see cref="ISchedulerBuilder.ConfigureOptions"/> call the
    /// resolved options carry the documented default (1s backoff) — locking in the default.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ConfigureOptions_Default_RestartBackoffIsOneSecond()
    {
        var provider = new ServiceCollection().AddScheduler().BuildServiceProvider();

        var options = provider.GetRequiredService<SchedulerOptions>();

        await Assert.That(options.RestartBackoff).IsEqualTo(TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Verifies multiple <see cref="ISchedulerBuilder.ConfigureOptions"/> calls compose
    /// in order — a later call sees and overrides the values an earlier one set.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ConfigureOptions_CalledTwice_ComposesInOrder()
    {
        var provider = new ServiceCollection()
            .AddScheduler(builder =>
            {
                builder.ConfigureOptions(options =>
                {
                    options.RestartBackoff = TimeSpan.FromSeconds(2);
                    options.MaxRestartsInWindow = 4;
                });
                builder.ConfigureOptions(options => options.MaxRestartsInWindow = 7);
            })
            .BuildServiceProvider();

        var options = provider.GetRequiredService<SchedulerOptions>();

        // The first call's RestartBackoff survives; the second call overrides MaxRestartsInWindow.
        await Assert.That(options.RestartBackoff).IsEqualTo(TimeSpan.FromSeconds(2));
        await Assert.That(options.MaxRestartsInWindow).IsEqualTo(7);
    }

    /// <summary>
    /// Verifies <see cref="ISchedulerBuilder.ConfigureOptions"/> composes alongside
    /// <c>AddInlineJob</c> and <c>UseStore</c> on the same builder.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ConfigureOptions_ComposesWithAddJobAndUseStore()
    {
        var services = new ServiceCollection();
        services.AddSingleton<NoopStore>();
        services.AddScheduler(builder =>
        {
            builder.ConfigureOptions(options => options.RestartBackoff = TimeSpan.FromSeconds(3));
            builder.AddInlineJob("composed-job")
                .Every(TimeSpan.FromMinutes(5))
                .Run((_, _) => ValueTask.CompletedTask);
            builder.UseStore<NoopStore>();
        });
        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<SchedulerOptions>();
        var store = provider.GetRequiredService<IScheduleStore>();
        var definitions = provider.GetRequiredService<SchedulerJobDefinitions>();

        await Assert.That(options.RestartBackoff).IsEqualTo(TimeSpan.FromSeconds(3));
        await Assert.That(store).IsTypeOf<NoopStore>();
        await Assert.That(definitions.Definitions.Any(d => d.Name == "composed-job")).IsTrue();
    }

    /// <summary>
    /// Verifies the cross-field fault-recovery constraint is enforced at
    /// <c>AddScheduler</c>: a backoff × restart budget that meets or exceeds the window
    /// throws a helpful exception naming the offending properties.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AddScheduler_BackoffTimesMaxRestartsExceedsWindow_Throws()
    {
        var act = () => new ServiceCollection().AddScheduler(builder =>
            builder.ConfigureOptions(options =>
            {
                // 10s × 6 = 60s >= 60s window: faults slide out faster than they accumulate.
                options.RestartBackoff = TimeSpan.FromSeconds(10);
                options.MaxRestartsInWindow = 6;
                options.RestartWindow = TimeSpan.FromSeconds(60);
            }));

        var exception = await Assert.That(act).Throws<InvalidOperationException>();
        await Assert.That(exception!.Message).Contains(nameof(SchedulerOptions.RestartBackoff));
        await Assert.That(exception.Message).Contains(nameof(SchedulerOptions.MaxRestartsInWindow));
        await Assert.That(exception.Message).Contains(nameof(SchedulerOptions.RestartWindow));
    }

    /// <summary>
    /// Verifies a negative <see cref="SchedulerOptions.RestartBackoff"/> fails the basic
    /// sanity guard at <c>AddScheduler</c> with a message naming the property.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AddScheduler_NegativeRestartBackoff_Throws()
    {
        var act = () => new ServiceCollection().AddScheduler(builder =>
            builder.ConfigureOptions(options => options.RestartBackoff = TimeSpan.FromSeconds(-1)));

        var exception = await Assert.That(act).Throws<InvalidOperationException>();
        await Assert.That(exception!.Message).Contains(nameof(SchedulerOptions.RestartBackoff));
    }

    /// <summary>
    /// Verifies <see cref="ISchedulerBuilder.ConfigureOptions"/> guards a null callback.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ConfigureOptions_NullCallback_Throws()
    {
        var act = () => new ServiceCollection().AddScheduler(builder =>
            builder.ConfigureOptions(null!));

        await Assert.That(act).Throws<ArgumentNullException>();
    }

    private sealed class NoopStore : IScheduleStore
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
