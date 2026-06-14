// =============================================================================
// <copyright file="CronCadenceTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;

namespace Bifrost.Tests.Scheduling.Cadences;

/// <summary>
/// Tests for <see cref="CronCadence"/> (Task 12): every-minute and daily occurrence
/// projection, time-zone handling (supplied and UTC default), fail-fast parsing, and
/// the <see cref="Cadence.Cron(string, TimeZoneInfo?)"/> factory.
/// </summary>
public sealed class CronCadenceTests
{
    /// <summary>
    /// Verifies an every-minute expression projects to the next whole-minute boundary.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ComputeNextFire_EveryMinute_ReturnsNextMinuteBoundary()
    {
        var cadence = new CronCadence("* * * * *", TimeZoneInfo.Utc);
        var now = new DateTimeOffset(2026, 6, 13, 12, 0, 30, TimeSpan.Zero);

        var next = cadence.ComputeNextFire(lastFiredAt: null, now: now);

        await Assert.That(next).IsEqualTo(new DateTimeOffset(2026, 6, 13, 12, 1, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// Verifies a daily 9am expression projects to the next 9am.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ComputeNextFire_DailyNineAm_ReturnsNextNineAm()
    {
        var cadence = new CronCadence("0 9 * * *", TimeZoneInfo.Utc);
        var now = new DateTimeOffset(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

        var next = cadence.ComputeNextFire(lastFiredAt: null, now: now);

        await Assert.That(next).IsEqualTo(new DateTimeOffset(2026, 6, 14, 9, 0, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// Verifies the cadence evaluates the expression in a supplied time zone: daily
    /// 9am New York time resolves to 13:00 UTC (EDT, June).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ComputeNextFire_RespectsSuppliedTimeZone()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var cadence = new CronCadence("0 9 * * *", newYork);
        // 12:00 UTC on 2026-06-13 is 08:00 EDT — next 9am EDT is 13:00 UTC same day.
        var now = new DateTimeOffset(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

        var next = cadence.ComputeNextFire(lastFiredAt: null, now: now);

        await Assert.That(next).IsEqualTo(new DateTimeOffset(2026, 6, 13, 13, 0, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// Verifies the cadence defaults to UTC when no time zone is supplied.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ComputeNextFire_DefaultsToUtc_WhenNoTimeZoneGiven()
    {
        var cadence = new CronCadence("0 9 * * *", timeZone: null);
        var now = new DateTimeOffset(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

        var next = cadence.ComputeNextFire(lastFiredAt: null, now: now);

        // UTC default: next 9am UTC is the following day.
        await Assert.That(next).IsEqualTo(new DateTimeOffset(2026, 6, 14, 9, 0, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// Verifies an invalid cron expression is rejected at construction (fail-fast).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Construction_InvalidExpression_Throws()
    {
        await Assert.That(() => new CronCadence("not a cron", TimeZoneInfo.Utc))
            .ThrowsException();
    }

    /// <summary>
    /// Verifies a null cron expression is rejected at construction.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Construction_NullExpression_Throws()
    {
        await Assert.That(() => new CronCadence(null!, TimeZoneInfo.Utc))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies <see cref="Cadence.Cron(string, TimeZoneInfo?)"/> builds a
    /// <see cref="CronCadence"/> carrying the supplied expression.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Cadence_Cron_ReturnsCronCadence()
    {
        var cadence = Cadence.Cron("0 9 * * *");

        await Assert.That(cadence).IsTypeOf<CronCadence>();
        await Assert.That(((CronCadence)cadence).Expression).IsEqualTo("0 9 * * *");
    }

    /// <summary>
    /// Verifies two cadences built from the same expression and time zone compare
    /// equal — the cached parsed expression must not leak into value equality, since
    /// <see cref="JobRecord"/> equality depends on cadence equality.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ValueEquality_IgnoresCachedParse()
    {
        var a = new CronCadence("0 9 * * *", TimeZoneInfo.Utc);
        var b = new CronCadence("0 9 * * *", TimeZoneInfo.Utc);

        await Assert.That(a).IsEqualTo(b);
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }
}
