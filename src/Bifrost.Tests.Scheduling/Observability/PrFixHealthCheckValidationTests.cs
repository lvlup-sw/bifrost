// =============================================================================
// <copyright file="PrFixHealthCheckValidationTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Observability;

using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.Observability;

/// <summary>
/// Regression tests for PR #25 CodeRabbit FIX C2: the
/// <see cref="SchedulerHealthCheck"/> constructor must reject a non-positive
/// <c>expectedInterval</c> (a zero or negative tick interval makes the staleness
/// window meaningless) and a <c>failureRateThreshold</c> outside <c>[0, 1]</c> (a
/// rate is a fraction). Valid boundary values are accepted.
/// </summary>
public sealed class PrFixHealthCheckValidationTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan ValidInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Verifies a zero expected interval is rejected at construction.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Ctor_ZeroExpectedInterval_Throws()
    {
        await Assert.That(() => NewCheck(expectedInterval: TimeSpan.Zero))
            .Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Verifies a negative expected interval is rejected at construction.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Ctor_NegativeExpectedInterval_Throws()
    {
        await Assert.That(() => NewCheck(expectedInterval: TimeSpan.FromSeconds(-1)))
            .Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Verifies a failure-rate threshold below zero is rejected at construction.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Ctor_NegativeFailureRateThreshold_Throws()
    {
        await Assert.That(() => NewCheck(failureRateThreshold: -0.01))
            .Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Verifies a failure-rate threshold above one is rejected at construction.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Ctor_FailureRateThresholdAboveOne_Throws()
    {
        await Assert.That(() => NewCheck(failureRateThreshold: 1.01))
            .Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Verifies the smallest positive interval and the threshold endpoints 0 and 1 are
    /// all accepted as valid boundary inputs.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Ctor_ValidBoundaryValues_AreAccepted()
    {
        await Assert.That(() => NewCheck(expectedInterval: TimeSpan.FromTicks(1)))
            .ThrowsNothing();
        await Assert.That(() => NewCheck(failureRateThreshold: 0.0))
            .ThrowsNothing();
        await Assert.That(() => NewCheck(failureRateThreshold: 1.0))
            .ThrowsNothing();
    }

    private static SchedulerHealthCheck NewCheck(
        TimeSpan? expectedInterval = null,
        double failureRateThreshold = SchedulerHealthCheck.DefaultFailureRateThreshold)
        => new(
            new TickHealthMonitor(),
            new StubFaultSource(),
            new FakeTimeProvider(Start),
            expectedInterval ?? ValidInterval,
            failureRateThreshold);

    private sealed class StubFaultSource : ISchedulerFaultSource
    {
        public bool IsFaulted => false;
    }
}
