// =============================================================================
// <copyright file="BufferCapacityTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Concurrency;

namespace Bifrost.Tests.Concurrency;

/// <summary>
/// The buffering dial on the constructor: the four-argument overload stores the buffer capacity it is
/// given (exposed through <c>BufferCapacityForTest</c>), and the other overloads default it to <c>0</c>
/// (buffering off).
/// </summary>
public class BufferCapacityTests
{
    /// <summary>The four-argument constructor honors an explicit buffer capacity.</summary>
    [Test]
    [Arguments(0, 0)]
    [Arguments(1, 1)]
    [Arguments(16, 16)]
    public async Task Ctor_BufferCapacity_HonorsExplicitValue(int requested, int expected)
    {
        var queue = new ConcurrentPriorityQueue<int, int>(boundedCapacity: -1, stickiness: 1, bufferCapacity: requested);

        await Assert.That(queue.BufferCapacityForTest).IsEqualTo(expected);
    }

    /// <summary>The default overloads leave buffering off (capacity 0).</summary>
    [Test]
    public async Task Ctor_DefaultOverloads_DisableBuffering()
    {
        await Assert.That(new ConcurrentPriorityQueue<int, int>().BufferCapacityForTest).IsEqualTo(0).Because(
            "the parameterless constructor leaves buffering off");
    }
}
