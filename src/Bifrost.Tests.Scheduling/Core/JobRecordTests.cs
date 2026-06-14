// =============================================================================
// <copyright file="JobRecordTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;

namespace Bifrost.Tests.Scheduling.Core;

/// <summary>
/// Tests for the persisted <see cref="JobRecord"/> contract (Task 6): positional
/// construction, non-destructive <c>with</c> mutation, and record value equality.
/// </summary>
public sealed class JobRecordTests
{
    /// <summary>
    /// A concrete <see cref="Cadence"/> double — the stub is abstract, so tests
    /// need a trivially subclassable record to construct a <see cref="JobRecord"/>.
    /// </summary>
    private sealed record FakeCadence : Cadence;

    private static JobRecord NewRecord(
        JobState state = JobState.Running,
        Cadence? cadence = null)
        => new(
            Name: "nightly-report",
            Cadence: cadence ?? new FakeCadence(),
            MissedFirePolicy: MissedFirePolicy.Coalesce,
            State: state,
            LastFiredAt: new DateTimeOffset(2026, 6, 13, 2, 0, 0, TimeSpan.Zero),
            NextFireAt: new DateTimeOffset(2026, 6, 14, 2, 0, 0, TimeSpan.Zero),
            DispatchKind: "orchestrator",
            DispatcherTypeName: "Acme.NightlyReportJob",
            Metadata: new Dictionary<string, string> { ["tenant"] = "acme" });

    /// <summary>
    /// Verifies positional construction sets every property.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Construction_SetsAllProperties()
    {
        var cadence = new FakeCadence();
        var record = NewRecord(cadence: cadence);

        await Assert.That(record.Name).IsEqualTo("nightly-report");
        await Assert.That(record.Cadence).IsEqualTo((Cadence)cadence);
        await Assert.That(record.MissedFirePolicy).IsEqualTo(MissedFirePolicy.Coalesce);
        await Assert.That(record.State).IsEqualTo(JobState.Running);
        await Assert.That(record.LastFiredAt)
            .IsEqualTo(new DateTimeOffset(2026, 6, 13, 2, 0, 0, TimeSpan.Zero));
        await Assert.That(record.NextFireAt)
            .IsEqualTo(new DateTimeOffset(2026, 6, 14, 2, 0, 0, TimeSpan.Zero));
        await Assert.That(record.DispatchKind).IsEqualTo("orchestrator");
        await Assert.That(record.DispatcherTypeName).IsEqualTo("Acme.NightlyReportJob");
        await Assert.That(record.Metadata["tenant"]).IsEqualTo("acme");
    }

    /// <summary>
    /// Verifies a <c>with</c> expression produces a new record whose
    /// <see cref="JobRecord.State"/> differs while all other components are equal.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task With_NonDestructiveMutationOfState()
    {
        var running = NewRecord(state: JobState.Running);

        var paused = running with { State = JobState.Paused };

        await Assert.That(paused.State).IsEqualTo(JobState.Paused);
        await Assert.That(running.State).IsEqualTo(JobState.Running);
        await Assert.That(paused.Name).IsEqualTo(running.Name);
        await Assert.That(paused.Cadence).IsEqualTo(running.Cadence);
        await Assert.That(paused).IsNotEqualTo(running);
    }

    /// <summary>
    /// Verifies record value equality: two records built from identical
    /// components compare equal.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ValueEquality_OfEqualRecords()
    {
        var cadence = new FakeCadence();
        var metadata = new Dictionary<string, string> { ["tenant"] = "acme" };

        var a = NewRecord(cadence: cadence) with { Metadata = metadata };
        var b = NewRecord(cadence: cadence) with { Metadata = metadata };

        await Assert.That(a).IsEqualTo(b);
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }

    /// <summary>
    /// Verifies <see cref="JobRecord"/> implements the synthesized
    /// <see cref="IEquatable{T}"/> from the record declaration.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ImplementsIEquatable()
    {
        // Share the Cadence and Metadata instances: record equality compares
        // reference-typed members (the cadence record and the dictionary) by
        // their own Equals — the dictionary's is reference identity — so equal
        // records must carry the same instances of each.
        var cadence = new FakeCadence();
        var metadata = new Dictionary<string, string> { ["tenant"] = "acme" };

        IEquatable<JobRecord> a = NewRecord(cadence: cadence) with { Metadata = metadata };
        var b = NewRecord(cadence: cadence) with { Metadata = metadata };

        await Assert.That(a.Equals(b)).IsTrue();
    }
}
