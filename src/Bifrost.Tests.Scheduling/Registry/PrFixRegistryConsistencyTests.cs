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
/// FIX A1 — <see cref="ScheduleRegistry.RegisterAsync"/> must claim the in-memory
/// slot BEFORE persisting, so a losing concurrent same-name registrant never
/// writes its (different) payload to the store. The store and the in-memory
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
    /// FIX A1. Two concurrent <see cref="ScheduleRegistry.RegisterAsync"/> calls for
    /// the SAME name with DIFFERENT payloads: exactly one succeeds and the other
    /// throws <see cref="DuplicateJobNameException"/>. Critically, the LOSER must NOT
    /// have written its payload to the store — the store and the in-memory registry
    /// must agree on the winner's payload. With the buggy persist-first ordering, the
    /// loser's blind upsert can leave loser data in the store; the fix claims the
    /// in-memory slot first so the loser never reaches the store.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Register_ConcurrentSameNameDifferentPayloads_LoserNeverWritesStore()
    {
        // A gating store: SaveAsync blocks on a barrier so BOTH registrants would be
        // mid-Save simultaneously under the buggy persist-first ordering. It records
        // every (name -> cadence) that is actually persisted, so we can prove the
        // loser never wrote.
        var store = new GatingStore();
        var registry = new ScheduleRegistry(store, new FakeTimeProvider(Now));

        // Two distinct payloads, distinguished by their interval. The "winner" is
        // whichever TryAdd succeeds; the test does not presume which.
        var cadenceA = new IntervalCadence(TimeSpan.FromHours(1));
        var cadenceB = new IntervalCadence(TimeSpan.FromHours(2));

        var taskA = Task.Run(async () =>
        {
            try
            {
                await registry.RegisterAsync("daily-report", cadenceA, MissedFirePolicy.Coalesce, new StubDispatcher())
                    .ConfigureAwait(false);
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
                await registry.RegisterAsync("daily-report", cadenceB, MissedFirePolicy.Coalesce, new StubDispatcher())
                    .ConfigureAwait(false);
                return (Won: true, Cadence: (Cadence)cadenceB);
            }
            catch (DuplicateJobNameException)
            {
                return (Won: false, Cadence: (Cadence)cadenceB);
            }
        });

        // Release the SaveAsync gate once both registrants have either entered Save or
        // bailed out before it. With the fix, only the winner reaches the gate; with
        // the bug, both do. The gate releases after a short bounded grace so the test
        // never wedges if only one registrant ever arrives.
        await store.ReleaseAfterArrivalGraceAsync().ConfigureAwait(false);

        var results = await Task.WhenAll(taskA, taskB).ConfigureAwait(false);

        // Exactly one winner, one loser.
        var winners = results.Where(r => r.Won).ToList();
        var losers = results.Where(r => !r.Won).ToList();
        await Assert.That(winners).HasCount(1);
        await Assert.That(losers).HasCount(1);

        var winnerCadence = winners[0].Cadence;

        // In-memory registry holds the winner's payload.
        var job = registry.GetJob("daily-report");
        await Assert.That(job).IsNotNull();
        await Assert.That(job!.Value.Cadence).IsEqualTo(winnerCadence);

        // The store holds the winner's payload — NOT the loser's. This is the core
        // FIX A1 assertion: a losing registrant must never have written the store.
        var persisted = await store.LoadAllAsync(CancellationToken.None).ConfigureAwait(false);
        await Assert.That(persisted).HasCount(1);
        await Assert.That(persisted[0].Cadence).IsEqualTo(winnerCadence);

        // Only the winner ever called SaveAsync: the loser claimed-and-failed without
        // touching the store. (Buggy persist-first ordering produces two saves.)
        await Assert.That(store.SaveCount).IsEqualTo(1);
    }

    /// <summary>
    /// FIX A2. After a successful commit (store write + in-memory add), the post-commit
    /// wake command must still be posted even when the caller's token is canceled. The
    /// caller cancels their token immediately after <c>SaveAsync</c> commits; the wake
    /// command must still land on the registry's command channel.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Register_CallerCancelsAfterCommit_WakeCommandStillPosted()
    {
        using var cts = new CancellationTokenSource();

        // A store whose SaveAsync cancels the caller's token the instant the commit
        // happens — modelling a caller cancellation that races in right after durable
        // state is committed but before the post-commit wake is posted.
        var store = new CancelOnSaveStore(cts);
        var registry = new ScheduleRegistry(store, new FakeTimeProvider(Now));

        await registry.RegisterAsync(
                "daily-report",
                new IntervalCadence(TimeSpan.FromHours(1)),
                MissedFirePolicy.Coalesce,
                new StubDispatcher(),
                cts.Token)
            .ConfigureAwait(false);

        // The commit succeeded (store + in-memory), so the wake command MUST be on the
        // channel despite the caller token being canceled at the post-commit point.
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
    /// wrote. The gate is released by the test once both registrants have arrived,
    /// or after a bounded grace if only one ever arrives (the fixed code path).
    /// </summary>
    private sealed class GatingStore : IScheduleStore
    {
        private readonly ConcurrentDictionary<string, JobRecord> jobs = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim gate = new(0);
        private int arrivals;
        private int saveCount;

        public int SaveCount => Volatile.Read(ref this.saveCount);

        public int Arrivals => Volatile.Read(ref this.arrivals);

        public async ValueTask SaveAsync(JobRecord job, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(job);

            // Count arrival so the test can coordinate the gate release.
            Interlocked.Increment(ref this.arrivals);

            await this.gate.WaitAsync(ct).ConfigureAwait(false);

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
        /// Releases the SaveAsync gate after a short bounded grace, by which point any
        /// registrant that is going to reach SaveAsync has done so (the winner under
        /// the fix; both registrants under the bug). Releases generously so no
        /// registrant blocks. The grace is bounded so the test never wedges.
        /// </summary>
        public async Task ReleaseAfterArrivalGraceAsync()
        {
            // Short bounded grace for registrants to reach (or bail before) SaveAsync.
            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);

            // Release enough permits to unblock any waiter (1 under the fix, 2 under the
            // bug) plus slack.
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

        public ValueTask SaveAsync(JobRecord job, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(job);

            this.jobs[job.Name] = job;

            // Commit done: cancel the caller token now. The post-commit wake must not
            // observe this cancellation (FIX A2 passes CancellationToken.None there).
            // Synchronous Cancel is intentional here — this is a deterministic test seam
            // that fires the cancellation exactly at the post-commit boundary.
#pragma warning disable VSTHRD103 // Cancel synchronously blocks
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
