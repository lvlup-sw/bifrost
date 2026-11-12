// =============================================================================
// <copyright file="PriorityArithmeticBoundaryTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Queues;

namespace Bifrost.Tests.Queues;

/// <summary>Integer-unit and overflow boundaries for priority conversion.</summary>
public class PriorityArithmeticBoundaryTests
{
    /// <summary>Window conversion truncates once in exact units, without floating-point rounding.</summary>
    [Test]
    [Arguments(10000000L)]
    [Arguments(1000000000L)]
    [Arguments(3L)]
    public async Task FractionalWindowsMatchIntegerOracle(long frequency)
    {
        for (var ticks = 1; ticks <= 1000; ticks++)
        {
            var options = new PriorityDispatchOptions { InteractiveBoostWindow = TimeSpan.FromTicks(ticks), BatchPenaltyWindow = TimeSpan.FromTicks(ticks) };
            var boosts = PriorityKey.Precompute(options, frequency);
            var expected = (long)((Int128)ticks * frequency / TimeSpan.TicksPerSecond);
            await Assert.That(boosts.InteractiveBoostTicks).IsEqualTo(expected);
            await Assert.That(boosts.BatchBoostTicks).IsEqualTo(-expected);
        }
    }

    /// <summary>Unrepresentable boosts are rejected before constructing a queue.</summary>
    [Test]
    public async Task UnrepresentableBoostIsRejected()
    {
        await Assert.That(() => PriorityKey.Precompute(new PriorityDispatchOptions
        {
            InteractiveBoostWindow = TimeSpan.MaxValue,
        }, 1000000000)).Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>Extreme timestamps cannot reverse priority through signed wraparound.</summary>
    [Test]
    public async Task ExtremeKeysSaturateWithoutReversingUrgency()
    {
        var boosts = PriorityKey.Precompute(new PriorityDispatchOptions
        {
            InteractiveBoostWindow = TimeSpan.FromTicks(10), BatchPenaltyWindow = TimeSpan.FromTicks(10),
        }, TimeSpan.TicksPerSecond);
        var interactive = new WorkEnvelope<int>(1, WorkClass.Interactive, long.MinValue + 1);
        var batch = new WorkEnvelope<int>(2, WorkClass.Batch, long.MaxValue - 1);
        await Assert.That(PriorityKey.Compute(in interactive, in boosts)).IsEqualTo(long.MinValue);
        await Assert.That(PriorityKey.Compute(in batch, in boosts)).IsEqualTo(long.MaxValue);
    }
}
