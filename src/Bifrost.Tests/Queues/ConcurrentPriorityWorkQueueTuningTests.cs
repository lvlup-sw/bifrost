// =============================================================================
// <copyright file="ConcurrentPriorityWorkQueueTuningTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Queues;

namespace Bifrost.Tests.Queues;

/// <summary>
/// The priority work queue resolves its <see cref="PriorityDispatchOptions.CpqTuning"/> profile (and the
/// element's reference-ness) into the stickiness and buffer capacity it builds the MultiQueue with.
/// </summary>
public sealed class ConcurrentPriorityWorkQueueTuningTests
{
    private const long TestTimestampFrequency = 1_000;

    /// <summary>Default options on reference-bearing work buffer at the baseline stickiness.</summary>
    [Test]
    public async Task DefaultOptions_ReferenceWork_BuffersAtBaselineStickiness()
    {
        using var queue = new ConcurrentPriorityWorkQueue<string>(8, new PriorityDispatchOptions(), TestTimestampFrequency);

        await Assert.That(queue.ResolvedStickinessForTest).IsEqualTo(1);
        await Assert.That(queue.ResolvedBufferCapacityForTest).IsEqualTo(16);
    }

    /// <summary>Default options on pure value-type work leave buffering off.</summary>
    [Test]
    public async Task DefaultOptions_ValueWork_DisablesBuffering()
    {
        using var queue = new ConcurrentPriorityWorkQueue<int>(8, new PriorityDispatchOptions(), TestTimestampFrequency);

        await Assert.That(queue.ResolvedStickinessForTest).IsEqualTo(1);
        await Assert.That(queue.ResolvedBufferCapacityForTest).IsEqualTo(0);
    }

    /// <summary>The LowConcurrency profile raises stickiness and turns buffering on.</summary>
    [Test]
    public async Task LowConcurrencyProfile_RaisesStickinessAndBuffers()
    {
        var options = new PriorityDispatchOptions { CpqTuning = CpqTuningProfile.LowConcurrency };
        using var queue = new ConcurrentPriorityWorkQueue<int>(8, options, TestTimestampFrequency);

        await Assert.That(queue.ResolvedStickinessForTest).IsEqualTo(4);
        await Assert.That(queue.ResolvedBufferCapacityForTest).IsEqualTo(16);
    }

    /// <summary>The StrictOrdering profile keeps baseline stickiness and leaves buffering off.</summary>
    [Test]
    public async Task StrictOrderingProfile_BaselineStickinessWithoutBuffering()
    {
        var options = new PriorityDispatchOptions { CpqTuning = CpqTuningProfile.StrictOrdering };
        using var queue = new ConcurrentPriorityWorkQueue<string>(8, options, TestTimestampFrequency);

        await Assert.That(queue.ResolvedStickinessForTest).IsEqualTo(1);
        await Assert.That(queue.ResolvedBufferCapacityForTest).IsEqualTo(0);
    }

    /// <summary><see cref="PriorityDispatchOptions.CpqTuning"/> defaults to Balanced.</summary>
    [Test]
    public async Task CpqTuning_DefaultsToBalanced()
    {
        await Assert.That(new PriorityDispatchOptions().CpqTuning).IsEqualTo(CpqTuningProfile.Balanced);
    }
}
