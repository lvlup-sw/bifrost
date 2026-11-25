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
    /// <remarks>
    /// The test resolves the harness as the interface, not the concrete type, so it exercises the
    /// contract and not an implementation detail. It waits for idle (loop started, job registered)
    /// and then advances exactly the interval. The job must fire before <c>AdvanceAsync</c>
    /// returns.
    /// </remarks>
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
            await hosted.StartAsync(CancellationToken.None);
        }

        try
        {
            ISchedulerTestHarness harness = provider.GetRequiredService<ISchedulerTestHarness>();

            await harness.WaitForIdleAsync(TestTimeout);

            await harness.AdvanceAsync(TimeSpan.FromMinutes(5));

            var completed = await Task.WhenAny(fired.Task, Task.Delay(TestTimeout));
            await Assert.That(completed == fired.Task).IsTrue();
        }
        finally
        {
            foreach (var hosted in provider.GetServices<IHostedService>().Reverse())
            {
                await hosted.StopAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Verifies that <see cref="ISchedulerTestHarness.FireDueJobsAsync"/> fires only
    /// jobs due at or before the current fake-clock instant, leaving future jobs
    /// unfired. The clock is advanced to make one job due; a second job scheduled far
    /// in the future is not fired. The harness is resolved as the interface type via DI.
    /// </summary>
    /// <remarks>
    /// <c>farFuture</c> is a fixed timestamp that is always later than the start of the fake clock.
    /// The interval job fires after 1 hour, so it becomes due when the test advances the clock by 1
    /// hour. The far-future job stays later than any clock advance in this test. The test waits for
    /// idle (both jobs registered, tick loop at rest), advances the clock by exactly 1 hour, and
    /// calls <c>FireDueJobsAsync</c>. Only the due job may fire.
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task FireDueJobsAsync_OnlyFiresDueJobs_NotFutureJobs_ViaInterface()
    {
        var dueCount = 0;
        var futureCount = 0;

        var farFuture = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScheduler(scheduler =>
        {
            scheduler.AddInlineJob("due-after-advance")
                .Every(TimeSpan.FromHours(1))
                .Run((_, _) =>
                {
                    Interlocked.Increment(ref dueCount);
                    return ValueTask.CompletedTask;
                });

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
            await hosted.StartAsync(CancellationToken.None);
        }

        try
        {
            ISchedulerTestHarness harness = provider.GetRequiredService<ISchedulerTestHarness>();
            var fakeTime = provider.GetRequiredService<FakeTimeProvider>();

            await harness.WaitForIdleAsync(TestTimeout);

            fakeTime.Advance(TimeSpan.FromHours(1));

            await harness.FireDueJobsAsync();

            await Assert.That(Volatile.Read(ref dueCount)).IsGreaterThanOrEqualTo(1);
            await Assert.That(Volatile.Read(ref futureCount)).IsEqualTo(0);
        }
        finally
        {
            foreach (var hosted in provider.GetServices<IHostedService>().Reverse())
            {
                await hosted.StopAsync(CancellationToken.None);
            }
        }
    }
}
