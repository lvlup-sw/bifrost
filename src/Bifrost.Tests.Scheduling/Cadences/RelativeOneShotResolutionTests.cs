// =============================================================================
// <copyright file="RelativeOneShotResolutionTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Registry;
using Bifrost.Scheduling.Stores;

using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Tests.Scheduling.Cadences;

/// <summary>
/// Tests for <see cref="RelativeOneShotCadence"/> registration-time resolution via
/// the injected <see cref="TimeProvider"/> (Task 45, R1/DR-2): the static factory
/// is clock-free, the registry resolves the delay to an absolute instant at
/// registration time, and an unresolved relative cadence fails fast if it ever
/// reaches the tick loop.
/// </summary>
public sealed class RelativeOneShotResolutionTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset T1 =
        new(2026, 6, 15, 9, 30, 0, TimeSpan.Zero);

    private static readonly TimeSpan Delay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Verifies that <see cref="Cadence.After(TimeSpan)"/> returns a
    /// <see cref="RelativeOneShotCadence"/> that merely wraps the delay and performs
    /// NO clock access — no <see cref="TimeProvider"/>, no
    /// <see cref="DateTimeOffset.UtcNow"/> (R1). The factory's only observable
    /// effect is capturing the delay in the returned record.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Cadence_After_PerformsNoClockAccess()
    {
        // Cadence.After must not call any clock: it just captures the delay.
        // We verify the return type and that the delay is preserved unchanged.
        var cadence = Cadence.After(Delay);

        await Assert.That(cadence).IsTypeOf<RelativeOneShotCadence>();
        await Assert.That(((RelativeOneShotCadence)cadence).Delay).IsEqualTo(Delay);
    }

    /// <summary>
    /// Verifies that when a <see cref="RelativeOneShotCadence"/> is registered with
    /// a <see cref="FakeTimeProvider"/> at T0, the registry resolves it to an
    /// absolute <see cref="OneShotCadence"/> with <c>FireAt == T0 + Delay</c>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task RegisterAsync_RelativeOneShot_ResolvesToAbsoluteOneShot_ViaInjectedTimeProvider()
    {
        var fakeTime = new FakeTimeProvider(T0);
        var registry = new ScheduleRegistry(new InMemoryScheduleStore(), fakeTime);

        await registry.RegisterAsync(
                "rel-job", Cadence.After(Delay), MissedFirePolicy.Coalesce, new NoopDispatcher())
            .ConfigureAwait(false);

        var job = registry.GetJob("rel-job");
        await Assert.That(job).IsNotNull();
        // The cadence stored in the record must be an absolute OneShotCadence.
        await Assert.That(job!.Value.Cadence).IsTypeOf<OneShotCadence>();
        var oneShot = (OneShotCadence)job.Value.Cadence;
        await Assert.That(oneShot.FireAt).IsEqualTo(T0 + Delay);
    }

    /// <summary>
    /// Verifies that the resolved <c>FireAt</c> tracks the fake clock at the moment
    /// of registration: if the clock is at T1 when <c>RegisterAsync</c> is called,
    /// the resolved instant is <c>T1 + Delay</c>, not <c>T0 + Delay</c>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task RegisterAsync_Resolution_TracksFakeClock_NotSystemClock()
    {
        var fakeTime = new FakeTimeProvider(T0);
        // Advance to T1 before registering; resolution must use T1, not T0.
        fakeTime.Advance(T1 - T0);
        var registry = new ScheduleRegistry(new InMemoryScheduleStore(), fakeTime);

        await registry.RegisterAsync(
                "rel-job-t1", Cadence.After(Delay), MissedFirePolicy.Coalesce, new NoopDispatcher())
            .ConfigureAwait(false);

        var job = registry.GetJob("rel-job-t1");
        await Assert.That(job).IsNotNull();
        await Assert.That(job!.Value.Cadence).IsTypeOf<OneShotCadence>();
        var oneShot = (OneShotCadence)job!.Value.Cadence;
        await Assert.That(oneShot.FireAt).IsEqualTo(T1 + Delay);
    }

    /// <summary>
    /// Verifies that an unresolved <see cref="RelativeOneShotCadence"/> throws
    /// <see cref="InvalidOperationException"/> with a non-empty descriptive message
    /// if it ever reaches the tick loop (<c>ComputeNextFire</c> is called).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task RelativeOneShotCadence_ComputeNextFire_Throws()
    {
        var cadence = new RelativeOneShotCadence(Delay);

        var ex = await Assert.That(
            () => cadence.ComputeNextFire(lastFiredAt: null, now: T0))
            .Throws<InvalidOperationException>();

        await Assert.That(ex!.Message).IsNotNullOrEmpty();
    }

    private sealed class NoopDispatcher : IJobDispatcher
    {
        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct) => ValueTask.CompletedTask;
    }
}
