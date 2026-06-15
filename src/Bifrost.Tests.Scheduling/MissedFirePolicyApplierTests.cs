// =============================================================================
// <copyright file="MissedFirePolicyApplierTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Internal;

namespace Bifrost.Tests.Scheduling;

/// <summary>
/// Tests for <see cref="MissedFirePolicyApplier"/> (Task 14, DR-3): enumerating the
/// occurrences missed strictly after the last fire and up to now, then reconciling
/// that backlog according to the <see cref="MissedFirePolicy"/>.
/// </summary>
public sealed class MissedFirePolicyApplierTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Verifies <see cref="MissedFirePolicy.Coalesce"/> collapses multiple missed
    /// occurrences into a single catch-up instant — the most recent missed fire.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Coalesce_WithMultipleMissedFires_ReturnsSingleCatchUpInstant()
    {
        var interval = TimeSpan.FromMinutes(5);
        var cadence = new IntervalCadence(interval);
        // Last fired 17 minutes ago: missed occurrences at -12, -7, -2 minutes from now.
        var lastFired = Now - TimeSpan.FromMinutes(17);

        var missed = MissedFirePolicyApplier.ComputeMissedFires(
            cadence, lastFired, Now, MissedFirePolicy.Coalesce);

        await Assert.That(missed).HasCount(1);
        // The single catch-up is the most recent missed occurrence (-2 minutes).
        await Assert.That(missed[0]).IsEqualTo(lastFired + (3 * interval));
    }

    /// <summary>
    /// Verifies <see cref="MissedFirePolicy.FireAllMissed"/> returns every missed
    /// occurrence in chronological order.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task FireAllMissed_ReturnsAllMissedInstantsInOrder()
    {
        var interval = TimeSpan.FromMinutes(5);
        var cadence = new IntervalCadence(interval);
        // Last fired 17 minutes ago: missed occurrences at -12, -7, -2 minutes from now.
        var lastFired = Now - TimeSpan.FromMinutes(17);

        var missed = MissedFirePolicyApplier.ComputeMissedFires(
            cadence, lastFired, Now, MissedFirePolicy.FireAllMissed);

        await Assert.That(missed).HasCount(3);
        await Assert.That(missed[0]).IsEqualTo(lastFired + interval);
        await Assert.That(missed[1]).IsEqualTo(lastFired + (2 * interval));
        await Assert.That(missed[2]).IsEqualTo(lastFired + (3 * interval));
    }

    /// <summary>
    /// Verifies <see cref="MissedFirePolicy.SkipMissed"/> returns an empty list: the
    /// caller schedules the next future fire rather than replaying any backlog.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task SkipMissed_ReturnsEmpty()
    {
        var interval = TimeSpan.FromMinutes(5);
        var cadence = new IntervalCadence(interval);
        var lastFired = Now - TimeSpan.FromMinutes(17);

        var missed = MissedFirePolicyApplier.ComputeMissedFires(
            cadence, lastFired, Now, MissedFirePolicy.SkipMissed);

        await Assert.That(missed).IsEmpty();
    }

    /// <summary>
    /// Verifies that when no occurrence falls in the missed window
    /// (<c>lastFired + interval &gt; now</c>) the result is empty regardless of policy.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task NoMissedFires_ReturnsEmpty()
    {
        var interval = TimeSpan.FromMinutes(5);
        var cadence = new IntervalCadence(interval);
        // Last fired 1 minute ago: next occurrence is +4 minutes, strictly after now.
        var lastFired = Now - TimeSpan.FromMinutes(1);

        var missed = MissedFirePolicyApplier.ComputeMissedFires(
            cadence, lastFired, Now, MissedFirePolicy.FireAllMissed);

        await Assert.That(missed).IsEmpty();
    }

    /// <summary>
    /// Verifies a one-shot cadence has no missed-fire concept: an already-fired
    /// one-shot produces no further occurrences, so the result is empty.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task OneShotCadence_AlreadyFired_ReturnsEmpty()
    {
        var fireAt = Now - TimeSpan.FromMinutes(5);
        var cadence = new OneShotCadence(fireAt);
        // The one-shot has already fired (lastFiredAt set), so no occurrence remains.
        var lastFired = fireAt;

        var missed = MissedFirePolicyApplier.ComputeMissedFires(
            cadence, lastFired, Now, MissedFirePolicy.FireAllMissed);

        await Assert.That(missed).IsEmpty();
    }

    /// <summary>
    /// Verifies <see cref="MissedFirePolicy.FireAllMissed"/> caps the backlog at
    /// <c>maxCatchUpCount</c> when the number of missed occurrences exceeds it.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task FireAllMissed_ExceedingCap_IsCappedAtMaxCatchUpCount()
    {
        var interval = TimeSpan.FromMinutes(1);
        var cadence = new IntervalCadence(interval);
        // Last fired 500 minutes ago: ~500 missed occurrences, far beyond the default cap.
        var lastFired = Now - TimeSpan.FromMinutes(500);

        var missed = MissedFirePolicyApplier.ComputeMissedFires(
            cadence, lastFired, Now, MissedFirePolicy.FireAllMissed);

        await Assert.That(missed).HasCount(100);
    }

    /// <summary>
    /// Verifies the explicit <c>maxCatchUpCount</c> argument overrides the default cap.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task FireAllMissed_WithExplicitCap_HonoursIt()
    {
        var interval = TimeSpan.FromMinutes(1);
        var cadence = new IntervalCadence(interval);
        var lastFired = Now - TimeSpan.FromMinutes(50);

        var missed = MissedFirePolicyApplier.ComputeMissedFires(
            cadence, lastFired, Now, MissedFirePolicy.FireAllMissed, maxCatchUpCount: 10);

        await Assert.That(missed).HasCount(10);
    }
}
