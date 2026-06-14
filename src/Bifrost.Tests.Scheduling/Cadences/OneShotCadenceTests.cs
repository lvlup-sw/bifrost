// =============================================================================
// <copyright file="OneShotCadenceTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;

namespace Bifrost.Tests.Scheduling.Cadences;

/// <summary>
/// Tests for the one-shot cadences (Task 9): the absolute <see cref="OneShotCadence"/>,
/// the unresolved <see cref="RelativeOneShotCadence"/>, and the
/// <see cref="Cadence.At(DateTimeOffset)"/> / <see cref="Cadence.After(TimeSpan)"/>
/// factories.
/// </summary>
public sealed class OneShotCadenceTests
{
    private static readonly DateTimeOffset FireAt =
        new(2026, 6, 14, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Now =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Verifies a one-shot that has never fired returns its scheduled instant.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task OneShotCadence_ComputeNextFire_WhenLastFiredNull_ReturnsFireAt()
    {
        var cadence = new OneShotCadence(FireAt);

        var next = cadence.ComputeNextFire(lastFiredAt: null, now: Now);

        await Assert.That(next).IsEqualTo(FireAt);
    }

    /// <summary>
    /// Verifies a one-shot does not re-fire once it has already fired.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task OneShotCadence_ComputeNextFire_WhenLastFiredNotNull_ReturnsNull()
    {
        var cadence = new OneShotCadence(FireAt);

        var next = cadence.ComputeNextFire(lastFiredAt: FireAt, now: Now);

        await Assert.That(next).IsNull();
    }

    /// <summary>
    /// Verifies a one-shot scheduled in the past still surfaces its instant — the
    /// tick loop, not the cadence, decides how to reconcile a past fire.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task OneShotCadence_ComputeNextFire_WhenFireAtInPast_StillReturnsFireAt()
    {
        var past = Now - TimeSpan.FromHours(1);
        var cadence = new OneShotCadence(past);

        var next = cadence.ComputeNextFire(lastFiredAt: null, now: Now);

        await Assert.That(next).IsEqualTo(past);
    }

    /// <summary>
    /// Verifies <see cref="Cadence.At(DateTimeOffset)"/> builds a
    /// <see cref="OneShotCadence"/> carrying the supplied instant.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Cadence_At_ReturnsOneShotCadence()
    {
        var cadence = Cadence.At(FireAt);

        await Assert.That(cadence).IsTypeOf<OneShotCadence>();
        await Assert.That(((OneShotCadence)cadence).FireAt).IsEqualTo(FireAt);
    }

    /// <summary>
    /// Verifies <see cref="Cadence.After(TimeSpan)"/> builds a
    /// <see cref="RelativeOneShotCadence"/> that merely wraps the delay, performing
    /// no clock access (R1) — the registry resolves it to an absolute instant later.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Cadence_After_ReturnsRelativeOneShotCadence()
    {
        var delay = TimeSpan.FromMinutes(30);

        var cadence = Cadence.After(delay);

        await Assert.That(cadence).IsTypeOf<RelativeOneShotCadence>();
        await Assert.That(((RelativeOneShotCadence)cadence).Delay).IsEqualTo(delay);
    }

    /// <summary>
    /// Verifies an unresolved <see cref="RelativeOneShotCadence"/> throws if it ever
    /// reaches the tick loop — it must be resolved to an absolute one-shot first.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task RelativeOneShotCadence_ComputeNextFire_Throws()
    {
        var cadence = new RelativeOneShotCadence(TimeSpan.FromMinutes(30));

        await Assert.That(() => cadence.ComputeNextFire(lastFiredAt: null, now: Now))
            .Throws<InvalidOperationException>();
    }
}
