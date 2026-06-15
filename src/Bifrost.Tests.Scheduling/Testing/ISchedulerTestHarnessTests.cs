// =============================================================================
// <copyright file="ISchedulerTestHarnessTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.DependencyInjection;
using Bifrost.Scheduling.Testing;
using Bifrost.Scheduling.Testing.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.Testing;

/// <summary>
/// Behavioral contract tests for <see cref="ISchedulerTestHarness"/> (Task 35, DR-9):
/// the interface's three control primitives —
/// <see cref="ISchedulerTestHarness.AdvanceAsync"/>,
/// <see cref="ISchedulerTestHarness.FireDueJobsAsync"/>, and
/// <see cref="ISchedulerTestHarness.WaitForIdleAsync"/> — drive the tick loop
/// deterministically when obtained through the DI container via
/// <c>AddSchedulerTesting()</c>.
/// </summary>
public sealed class ISchedulerTestHarnessTests
{
    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Verifies that <see cref="ISchedulerTestHarness.AdvanceAsync"/> drives a due job
    /// to fire: registering an interval job and advancing the clock by exactly the
    /// interval causes the job's dispatcher to execute before <c>AdvanceAsync</c>
    /// returns. The harness is resolved as the interface type, not the concrete class.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task AdvanceAsync_DrivesJobToFire_ViaInterface()
    {
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScheduler(scheduler =>
            scheduler.AddInlineJob("harness-advance-test")
                .Every(TimeSpan.FromMinutes(5))
                .Run((_, _) =>
                {
                    fired.TrySetResult();
                    return ValueTask.CompletedTask;
                }));
        services.AddSchedulerTesting();

        await using var provider = services.BuildServiceProvider();
        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            await hosted.StartAsync(CancellationToken.None).ConfigureAwait(false);
        }

        try
        {
            // Resolve as the interface — not the concrete type — to exercise the
            // contract rather than an implementation detail.
            ISchedulerTestHarness harness = provider.GetRequiredService<ISchedulerTestHarness>();

            // Wait for idle (loop started, job registered).
            await harness.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            // Advance exactly the interval; the job must fire before AdvanceAsync returns.
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

    /// <summary>
    /// Verifies that <see cref="ISchedulerTestHarness.FireDueJobsAsync"/> fires only
    /// jobs due at or before the current fake-clock instant, leaving future jobs
    /// unfired. The clock is advanced to make one job due; a second job scheduled far
    /// in the future is not fired. The harness is resolved as the interface type via DI.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task FireDueJobsAsync_OnlyFiresDueJobs_NotFutureJobs_ViaInterface()
    {
        var dueCount = 0;
        var futureCount = 0;

        // A fixed timestamp far in the future — always beyond the fake clock's start.
        var farFuture = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScheduler(scheduler =>
        {
            // Due job: fires after 1 hour — will become due when we advance 1 hour.
            scheduler.AddInlineJob("due-after-advance")
                .Every(TimeSpan.FromHours(1))
                .Run((_, _) =>
                {
                    Interlocked.Increment(ref dueCount);
                    return ValueTask.CompletedTask;
                });

            // Future job: scheduled far beyond any clock advance in this test.
            scheduler.AddInlineJob("far-future-job")
                .At(farFuture)
                .Run((_, _) =>
                {
                    Interlocked.Increment(ref futureCount);
                    return ValueTask.CompletedTask;
                });
        });
        services.AddSchedulerTesting();

        await using var provider = services.BuildServiceProvider();
        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            await hosted.StartAsync(CancellationToken.None).ConfigureAwait(false);
        }

        try
        {
            ISchedulerTestHarness harness = provider.GetRequiredService<ISchedulerTestHarness>();
            var fakeTime = provider.GetRequiredService<FakeTimeProvider>();

            // Wait for idle: both jobs are registered and the tick loop is at rest.
            await harness.WaitForIdleAsync(TestTimeout).ConfigureAwait(false);

            // Advance the clock by exactly 1 hour — the interval job is now due;
            // the far-future job remains in the future.
            fakeTime.Advance(TimeSpan.FromHours(1));

            // FireDueJobsAsync must fire only the due job, not the future one.
            await harness.FireDueJobsAsync().ConfigureAwait(false);

            await Assert.That(Volatile.Read(ref dueCount)).IsGreaterThanOrEqualTo(1);
            await Assert.That(Volatile.Read(ref futureCount)).IsEqualTo(0);
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
