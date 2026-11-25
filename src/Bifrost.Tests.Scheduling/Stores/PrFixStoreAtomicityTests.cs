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
    /// <remarks>
    /// <para>
    /// Two writers contend on the same key. <c>SaveAsync</c> changes <c>State</c> to Paused, and
    /// <c>RecordFiredAsync</c> stamps the fire instants and copies <c>State</c> forward. A <see
    /// cref="Barrier"/> makes the read-modify-write windows of the two writers overlap as much as
    /// possible.
    /// </para>
    /// <para>
    /// The committed Paused state must never go back to Running through a stale read-modify-write.
    /// This is the exact discriminator: the state is always Paused under the CAS fix, and
    /// intermittently Running under the buggy blind write. The test does not assert the fire stamp.
    /// In a valid serialization where <c>RecordFiredAsync</c> lands before <c>SaveAsync</c>, the
    /// blind upsert of <c>SaveAsync</c> clears the fire fields, so the fire stamp is not a
    /// discriminator.
    /// </para>
    /// </remarks>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task RecordFired_RacingWithSave_DoesNotClobberConcurrentStateUpdate()
    {
        const int iterations = 2000;

        for (int i = 0; i < iterations; i++)
        {
            var store = new InMemoryScheduleStore();
            await store.SaveAsync(MakeJob("daily-report", JobState.Running), CancellationToken.None);

            using var barrier = new Barrier(2);

            var pause = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                await store.SaveAsync(MakeJob("daily-report", JobState.Paused), CancellationToken.None);
            });

            var fire = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                await store.RecordFiredAsync(
                        "daily-report",
                        Now,
                        Now + TimeSpan.FromHours(1),
                        CancellationToken.None);
            });

            await Task.WhenAll(pause, fire);

            var jobs = await store.LoadAllAsync(CancellationToken.None);
            var final = jobs.Single();

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

        await store.RecordFiredAsync("missing", Now, Now + TimeSpan.FromHours(1), CancellationToken.None);

        var jobs = await store.LoadAllAsync(CancellationToken.None);
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
