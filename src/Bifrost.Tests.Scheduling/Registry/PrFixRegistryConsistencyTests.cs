// =============================================================================
// <copyright file="PrFixRegistryConsistencyTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Registry;

using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.Registry;

/// <summary>
/// Regression tests for PR #25 CodeRabbit findings on
/// <see cref="ScheduleRegistry"/>:
/// <list type="bullet">
/// <item>
/// FIX A1 — <see cref="ScheduleRegistry.RegisterAsync"/> allows one of two
/// concurrent same-name registrations to persist its payload. The store and
/// registry must agree on the winner's payload.
/// </item>
/// <item>
/// FIX A2 — the post-commit wake command must still be posted even when the
/// caller's token is canceled after the durable+in-memory state is committed:
/// caller cancellation must not drop the tick-loop wake.
/// </item>
/// </list>
/// </summary>
public sealed class PrFixRegistryConsistencyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Exactly one of two concurrent <see cref="ScheduleRegistry.RegisterAsync"/> calls
    /// for the same name succeeds. The other throws <see cref="DuplicateJobNameException"/>.
    /// The store records one write, and its payload matches the in-memory winner.
    /// </summary>
    /// <remarks>
    /// A per-name mutation lease holds the second registration until the first completes.
    /// The duplicate-name check then rejects the second payload before it reaches the store.
    /// The store gate opens when the first save arrives, and the test accepts either payload
    /// as the winner.
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Register_ConcurrentSameNameDifferentPayloads_LoserNeverWritesStore()
    {
        var store = new GatingStore();
        var registry = new ScheduleRegistry(store, new FakeTimeProvider(Now));

        var cadenceA = new IntervalCadence(TimeSpan.FromHours(1));
        var cadenceB = new IntervalCadence(TimeSpan.FromHours(2));

        var taskA = Task.Run(async () =>
        {
            try
            {
                await registry.RegisterAsync("daily-report", cadenceA, MissedFirePolicy.Coalesce, new StubDispatcher());
                return (Won: true, Cadence: (Cadence)cadenceA);
            }
            catch (DuplicateJobNameException)
            {
                return (Won: false, Cadence: (Cadence)cadenceA);
            }
        });

        var taskB = Task.Run(async () =>
        {
            try
            {
                await registry.RegisterAsync("daily-report", cadenceB, MissedFirePolicy.Coalesce, new StubDispatcher());
                return (Won: true, Cadence: (Cadence)cadenceB);
            }
            catch (DuplicateJobNameException)
            {
                return (Won: false, Cadence: (Cadence)cadenceB);
            }
        });

        await store.ReleaseAfterFirstArrivalAsync(TimeSpan.FromSeconds(10));

        var results = await Task.WhenAll(taskA, taskB);

        var winners = results.Where(r => r.Won).ToList();
        var losers = results.Where(r => !r.Won).ToList();
        await Assert.That(winners).HasCount(1);
        await Assert.That(losers).HasCount(1);

        var winnerCadence = winners[0].Cadence;

        var job = registry.GetJob("daily-report");
        await Assert.That(job).IsNotNull();
        await Assert.That(job!.Value.Cadence).IsEqualTo(winnerCadence);

        var persisted = await store.LoadAllAsync(CancellationToken.None);
        await Assert.That(persisted).HasCount(1);
        await Assert.That(persisted[0].Cadence).IsEqualTo(winnerCadence);

        await Assert.That(store.SaveCount).IsEqualTo(1);
    }

    /// <summary>
    /// FIX A2. After a successful commit (store write + in-memory add), the post-commit
    /// wake command must still be posted even when the caller's token is canceled. The
    /// caller cancels their token immediately after <c>SaveAsync</c> commits; the wake
    /// command must still land on the registry's command channel.
    /// </summary>
    /// <remarks>
    /// <c>CancelOnSaveStore.SaveAsync</c> cancels the caller token the instant the commit happens.
    /// This models a caller cancellation that races in after the durable state is committed but
    /// before the post-commit wake is posted. The commit succeeded (store and in-memory), so the
    /// wake command must be on the channel even though the caller token was canceled at the
    /// post-commit point.
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Register_CallerCancelsAfterCommit_WakeCommandStillPosted()
    {
        using var cts = new CancellationTokenSource();

        var store = new CancelOnSaveStore(cts);
        var registry = new ScheduleRegistry(store, new FakeTimeProvider(Now));

        await registry.RegisterAsync(
                "daily-report",
                new IntervalCadence(TimeSpan.FromHours(1)),
                MissedFirePolicy.Coalesce,
                new StubDispatcher(),
                cts.Token);

        var read = registry.Commands.TryRead(out var command);
        await Assert.That(read).IsTrue();
        await Assert.That(command.Kind).IsEqualTo(RegistryCommandKind.Register);
        await Assert.That(command.JobName).IsEqualTo("daily-report");
    }

    private sealed class StubDispatcher : IJobDispatcher
    {
        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct) => ValueTask.CompletedTask;
    }

    /// <summary>
    /// A store whose <see cref="SaveAsync"/> blocks on a gate and records every
    /// persisted record, so a test can observe whether a losing registrant ever
    /// wrote. The test releases the gate when the arrival count shows that the first
    /// registrant has reached <see cref="SaveAsync"/>.
    /// </summary>
    private sealed class GatingStore : IScheduleStore
    {
        private readonly ConcurrentDictionary<string, JobRecord> jobs = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim gate = new(0);
        private int arrivals;
        private int saveCount;

        public int SaveCount => Volatile.Read(ref this.saveCount);

        public int Arrivals => Volatile.Read(ref this.arrivals);

        // Counts each arrival in arrivals (exposed as Arrivals) before it waits on the gate.
        // ReleaseAfterFirstArrivalAsync reads the count to release the gate.
        public async ValueTask SaveAsync(JobRecord job, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(job);

            Interlocked.Increment(ref this.arrivals);

            await this.gate.WaitAsync(ct);

            Interlocked.Increment(ref this.saveCount);
            this.jobs[job.Name] = job;
        }

        public ValueTask<IReadOnlyList<JobRecord>> LoadAllAsync(CancellationToken ct)
        {
            IReadOnlyList<JobRecord> snapshot = [.. this.jobs.Values];
            return ValueTask.FromResult(snapshot);
        }

        public ValueTask RecordFiredAsync(string jobName, DateTimeOffset firedAt, DateTimeOffset? nextFireAt, CancellationToken ct)
            => ValueTask.CompletedTask;

        public ValueTask DeleteAsync(string jobName, CancellationToken ct)
        {
            this.jobs.TryRemove(jobName, out _);
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// Waits until the first registrant has arrived in <see cref="SaveAsync"/>, then
        /// releases the gate. The wait is bounded by <paramref name="timeout"/> on the real
        /// wall clock, so a registrant that never arrives fails the test fast.
        /// </summary>
        /// <param name="timeout">The real-wall-clock bound on the wait for the first arrival.</param>
        /// <returns>A task that completes when the gate is released.</returns>
        /// <remarks>
        /// Extra permits let an unexpected second write complete so the save-count assertion can
        /// report it.
        /// </remarks>
        /// <exception cref="TimeoutException">No registrant arrived within <paramref name="timeout"/>.</exception>
        public async Task ReleaseAfterFirstArrivalAsync(TimeSpan timeout)
        {
            var deadline = Task.Delay(timeout, TimeProvider.System, CancellationToken.None);
            while (this.Arrivals < 1)
            {
                if (deadline.IsCompleted)
                {
                    throw new TimeoutException($"No registrant reached SaveAsync within {timeout}.");
                }

                await Task.Yield();
            }

            this.gate.Release(8);
        }
    }

    /// <summary>
    /// A store whose <see cref="SaveAsync"/> cancels the supplied token the moment the
    /// record is committed — modelling a caller cancellation landing exactly at the
    /// post-commit boundary, so the test can assert the wake command is still posted.
    /// </summary>
    private sealed class CancelOnSaveStore(CancellationTokenSource cts) : IScheduleStore
    {
        private readonly ConcurrentDictionary<string, JobRecord> jobs = new(StringComparer.Ordinal);

        // After the commit, the method cancels the caller token at once. The post-commit wake must
        // not observe this cancellation, because FIX A2 passes CancellationToken.None there. The
        // synchronous Cancel blocks (VSTHRD103) and is intentional: this deterministic test seam
        // fires the cancellation exactly at the post-commit boundary.
        public ValueTask SaveAsync(JobRecord job, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(job);

            this.jobs[job.Name] = job;

#pragma warning disable VSTHRD103
            cts.Cancel();
#pragma warning restore VSTHRD103
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<JobRecord>> LoadAllAsync(CancellationToken ct)
        {
            IReadOnlyList<JobRecord> snapshot = [.. this.jobs.Values];
            return ValueTask.FromResult(snapshot);
        }

        public ValueTask RecordFiredAsync(string jobName, DateTimeOffset firedAt, DateTimeOffset? nextFireAt, CancellationToken ct)
            => ValueTask.CompletedTask;

        public ValueTask DeleteAsync(string jobName, CancellationToken ct)
        {
            this.jobs.TryRemove(jobName, out _);
            return ValueTask.CompletedTask;
        }
    }
}
