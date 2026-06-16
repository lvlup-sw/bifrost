// =============================================================================
// <copyright file="PriorityBindingResolverTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Queues;

using TUnit.Core;

namespace Bifrost.Tests.Queues;

/// <summary>
/// Tests for <see cref="PriorityBindingResolver"/> (T4, T5, T6).
/// </summary>
[Property("Category", "Unit")]
public class PriorityBindingResolverTests
{
    // ─── T4: Explicit pass-through ───────────────────────────────────────────

    /// <summary>
    /// Verifies that explicit <see cref="PriorityBinding.Locking"/> always resolves
    /// to <see cref="DispatchStrategy.PriorityLocking"/>, regardless of processorCount
    /// or capacity.
    /// </summary>
    [Test]
    public async Task Resolve_ExplicitLocking_ReturnsPriorityLocking()
    {
        var result = PriorityBindingResolver.Resolve(PriorityBinding.Locking, processorCount: 4, capacity: 128);
        await Assert.That(result).IsEqualTo(DispatchStrategy.PriorityLocking);
    }

    /// <summary>
    /// Verifies that explicit <see cref="PriorityBinding.MultiQueue"/> always resolves
    /// to <see cref="DispatchStrategy.PriorityMultiQueue"/>, regardless of processorCount
    /// or capacity.
    /// </summary>
    [Test]
    public async Task Resolve_ExplicitMultiQueue_ReturnsPriorityMultiQueue()
    {
        var result = PriorityBindingResolver.Resolve(PriorityBinding.MultiQueue, processorCount: 4, capacity: 128);
        await Assert.That(result).IsEqualTo(DispatchStrategy.PriorityMultiQueue);
    }

    // ─── T5: Auto heuristic matrix ───────────────────────────────────────────

    /// <summary>
    /// cap=128, pc=16: n=RoundUpToPow2(64)=64; rankErr=5*64/6=53; 53 >= 64 → false → MultiQueue.
    /// </summary>
    [Test]
    public async Task Resolve_Auto_Cap128_Pc16_ReturnsMultiQueue()
    {
        // n = RoundUpToPow2(4*16) = RoundUpToPow2(64) = 64
        // 5*64 = 320, 3*128 = 384 → 320 < 384 → MultiQueue
        var result = PriorityBindingResolver.Resolve(PriorityBinding.Auto, processorCount: 16, capacity: 128);
        await Assert.That(result).IsEqualTo(DispatchStrategy.PriorityMultiQueue);
    }

    /// <summary>
    /// cap=128, pc=17: n=RoundUpToPow2(68)=128; 5*128=640 >= 3*128=384 → Locking.
    /// </summary>
    [Test]
    public async Task Resolve_Auto_Cap128_Pc17_ReturnsLocking()
    {
        // n = RoundUpToPow2(4*17) = RoundUpToPow2(68) = 128
        // 5*128 = 640, 3*128 = 384 → 640 >= 384 → Locking
        var result = PriorityBindingResolver.Resolve(PriorityBinding.Auto, processorCount: 17, capacity: 128);
        await Assert.That(result).IsEqualTo(DispatchStrategy.PriorityLocking);
    }

    /// <summary>
    /// cap=128, pc=32: n=RoundUpToPow2(128)=128; 5*128=640 >= 3*128=384 → Locking.
    /// </summary>
    [Test]
    public async Task Resolve_Auto_Cap128_Pc32_ReturnsLocking()
    {
        var result = PriorityBindingResolver.Resolve(PriorityBinding.Auto, processorCount: 32, capacity: 128);
        await Assert.That(result).IsEqualTo(DispatchStrategy.PriorityLocking);
    }

    /// <summary>
    /// cap=1024, pc=32: n=RoundUpToPow2(128)=128; 5*128=640, 3*1024=3072 → 640 &lt; 3072 → MultiQueue.
    /// </summary>
    [Test]
    public async Task Resolve_Auto_Cap1024_Pc32_ReturnsMultiQueue()
    {
        // n = RoundUpToPow2(4*32) = 128
        // 5*128 = 640, 3*1024 = 3072 → 640 < 3072 → MultiQueue
        var result = PriorityBindingResolver.Resolve(PriorityBinding.Auto, processorCount: 32, capacity: 1024);
        await Assert.That(result).IsEqualTo(DispatchStrategy.PriorityMultiQueue);
    }

    /// <summary>
    /// cap=32, pc=8: n=RoundUpToPow2(32)=32; 5*32=160 >= 3*32=96 → Locking.
    /// </summary>
    [Test]
    public async Task Resolve_Auto_Cap32_Pc8_ReturnsLocking()
    {
        // n = RoundUpToPow2(4*8) = 32
        // 5*32 = 160, 3*32 = 96 → 160 >= 96 → Locking
        var result = PriorityBindingResolver.Resolve(PriorityBinding.Auto, processorCount: 8, capacity: 32);
        await Assert.That(result).IsEqualTo(DispatchStrategy.PriorityLocking);
    }

    /// <summary>
    /// cap=32, pc=4: n=RoundUpToPow2(16)=16; 5*16=80, 3*32=96 → 80 &lt; 96 → MultiQueue.
    /// </summary>
    [Test]
    public async Task Resolve_Auto_Cap32_Pc4_ReturnsMultiQueue()
    {
        // n = RoundUpToPow2(4*4) = 16
        // 5*16 = 80, 3*32 = 96 → 80 < 96 → MultiQueue
        var result = PriorityBindingResolver.Resolve(PriorityBinding.Auto, processorCount: 4, capacity: 32);
        await Assert.That(result).IsEqualTo(DispatchStrategy.PriorityMultiQueue);
    }

    // ─── T6: SubQueueCountFor matches actual ConcurrentPriorityQueue sub-queue count ───
    // (test added here after T6 implementation adds the SubQueueCount accessor)
}
