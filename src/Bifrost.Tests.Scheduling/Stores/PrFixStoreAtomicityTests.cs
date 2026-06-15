// =============================================================================
// <copyright file="PrFixStoreAtomicityTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Stores;

namespace Bifrost.Tests.Scheduling.Stores;

/// <summary>
/// Regression test for PR #25 CodeRabbit finding FIX A3 on
/// <see cref="InMemoryScheduleStore.RecordFiredAsync"/>: the read-modify-write must
/// be atomic. The buggy version did <c>TryGetValue</c> (reading the current record)
/// then a blind indexer assignment of <c>existing with { fire fields }</c>. A
/// concurrent <see cref="InMemoryScheduleStore.SaveAsync"/> that mutated a different
/// field (for example, the lifecycle state) in the window between that read and the
/// blind write was clobbered — a classic lost update. The fix replaces the blind
/// write with a <c>TryUpdate</c> CAS retry loop so each fire update is applied on top
/// of the freshest record, re-reading and retrying on contention.
/// </summary>
public sealed class PrFixStoreAtomicityTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// FIX A3 — lost-update regression. Repeatedly races a
    /// <see cref="InMemoryScheduleStore.RecordFiredAsync"/> against a concurrent
    /// <see cref="InMemoryScheduleStore.SaveAsync"/> that flips the job from Running to
    /// Paused. In every valid serialization of these two writers the final state is
    /// Paused: <c>RecordFiredAsync</c> never authors a Running value of its own — it
    /// only ever copies forward whatever record it read. The ONLY way the final state
    /// is Running is a non-atomic read-modify-write: <c>RecordFiredAsync</c> reads the
    /// pre-Paused (Running) snapshot, then — after <c>SaveAsync</c> has committed
    /// Paused — blind-writes the stale Running snapshot back with the fire stamp,
    /// silently discarding the committed Paused. The <c>TryUpdate</c> CAS loop detects
    /// the changed record and retries against the freshest (Paused) record, so the
    /// Paused commit can never be clobbered. The buggy blind write trips this within a
    /// few iterations; the CAS loop holds on every iteration.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task RecordFired_RacingWithSave_DoesNotClobberConcurrentStateUpdate()
    {
        const int iterations = 2000;

        for (int i = 0; i < iterations; i++)
        {
            var store = new InMemoryScheduleStore();
            await store.SaveAsync(MakeJob("daily-report", JobState.Running), CancellationToken.None)
                .ConfigureAwait(false);

            // Two writers contend on the same key:
            //  - SaveAsync flips State -> Paused,
            //  - RecordFiredAsync stamps the fire instants (copying State forward).
            // A barrier maximizes overlap of the read-modify-write window.
            using var barrier = new Barrier(2);

            var pause = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                await store.SaveAsync(MakeJob("daily-report", JobState.Paused), CancellationToken.None)
                    .ConfigureAwait(false);
            });

            var fire = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                await store.RecordFiredAsync(
                        "daily-report",
                        Now,
                        Now + TimeSpan.FromHours(1),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            });

            await Task.WhenAll(pause, fire).ConfigureAwait(false);

            var jobs = await store.LoadAllAsync(CancellationToken.None).ConfigureAwait(false);
            var final = jobs.Single();

            // The committed Paused state must never be silently clobbered back to
            // Running by a stale read-modify-write. This is the exact discriminator:
            // ALWAYS Paused under the CAS fix; INTERMITTENTLY Running under the buggy
            // blind write. (The fire stamp is NOT asserted: a valid serialization where
            // RecordFiredAsync lands before SaveAsync legitimately leaves the fire
            // fields cleared by SaveAsync's blind upsert, so it is not a discriminator.)
            await Assert.That(final.State).IsEqualTo(JobState.Paused);
        }
    }

    /// <summary>
    /// FIX A3. Preserves the absent-key contract: recording a fire for a job that was
    /// never saved is a no-op (no phantom record created), unchanged by the CAS loop.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task RecordFired_AbsentKey_StaysNoOpUnderCas()
    {
        var store = new InMemoryScheduleStore();

        await store.RecordFiredAsync("missing", Now, Now + TimeSpan.FromHours(1), CancellationToken.None)
            .ConfigureAwait(false);

        var jobs = await store.LoadAllAsync(CancellationToken.None).ConfigureAwait(false);
        await Assert.That(jobs).IsEmpty();
    }

    private static JobRecord MakeJob(string name, JobState state)
        => new(
            Name: name,
            Cadence: new IntervalCadence(TimeSpan.FromHours(1)),
            MissedFirePolicy: MissedFirePolicy.Coalesce,
            State: state,
            LastFiredAt: null,
            NextFireAt: Now + TimeSpan.FromHours(1),
            DispatchKind: "custom",
            DispatcherTypeName: null,
            Metadata: new Dictionary<string, string>());
}
