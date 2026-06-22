// =============================================================================
// <copyright file="AddSchedulerTestingTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.DependencyInjection;
using Bifrost.Scheduling.Registry;
using Bifrost.Scheduling.Stores;
using Bifrost.Scheduling.Testing;
using Bifrost.Scheduling.Testing.DependencyInjection;
using Bifrost.Scheduling.TickEngine;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.Testing;

/// <summary>
/// Tests for <see cref="SchedulerTestingExtensions.AddSchedulerTesting"/> (Task 37,
/// DR-9): the extension replaces the <see cref="TimeProvider"/> with a
/// <see cref="FakeTimeProvider"/> (last-wins) and registers the
/// <see cref="ISchedulerTestHarness"/>, compatible with <c>AddScheduler</c>.
/// </summary>
public sealed class AddSchedulerTestingTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies that <c>AddSchedulerTesting</c> registers a <see cref="FakeTimeProvider"/>
    /// as the <see cref="TimeProvider"/> singleton, replacing any previously registered
    /// real provider.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AddSchedulerTesting_RegistersFakeTimeProvider()
    {
        var services = new ServiceCollection();
        services.AddScheduler();
        services.AddSchedulerTesting();

        await using var provider = services.BuildServiceProvider();
        var timeProvider = provider.GetRequiredService<TimeProvider>();

        await Assert.That(timeProvider).IsTypeOf<FakeTimeProvider>();
    }

    /// <summary>
    /// Verifies that <c>AddSchedulerTesting</c> registers <see cref="ISchedulerTestHarness"/>
    /// so it is resolvable from the container.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AddSchedulerTesting_RegistersISchedulerTestHarness()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScheduler();
        services.AddSchedulerTesting();

        await using var provider = services.BuildServiceProvider();
        var harness = provider.GetRequiredService<ISchedulerTestHarness>();

        await Assert.That(harness).IsNotNull();
        await Assert.That(harness).IsTypeOf<SchedulerTestHarness>();
    }

    /// <summary>
    /// Verifies that <c>AddSchedulerTesting</c> is compatible with <c>AddScheduler</c>:
    /// calling both in sequence produces a working scheduler that the harness can
    /// drive deterministically end-to-end.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AddSchedulerTesting_CompatibleWithAddScheduler()
    {
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScheduler(scheduler =>
            scheduler.AddInlineJob("test-job")
                .Every(TimeSpan.FromMinutes(5))
                .Run((_, _) =>
                {
                    fired.TrySetResult();
                    return ValueTask.CompletedTask;
                }));
        services.AddSchedulerTesting();

        await using var provider = services.BuildServiceProvider();

        try
        {
            // Start all hosted services (registration service + tick loop). Kept inside
            // the guarded scope so the finally stops any services already started if a
            // later StartAsync throws.
            foreach (var hosted in provider.GetServices<IHostedService>())
            {
                await hosted.StartAsync(CancellationToken.None).ConfigureAwait(false);
            }

            var harness = provider.GetRequiredService<ISchedulerTestHarness>();

            // Wait for initial idle (loop started, job registered).
            await harness.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            // Advance past the job's interval; it should fire.
            await harness.AdvanceAsync(TimeSpan.FromMinutes(5)).ConfigureAwait(false);

            var completed = await Task.WhenAny(fired.Task, Task.Delay(TestTimeout)).ConfigureAwait(false);
            await Assert.That(completed == fired.Task).IsTrue();
        }
        finally
        {
            foreach (var hosted in provider.GetServices<IHostedService>().Reverse())
            {
                await hosted.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
