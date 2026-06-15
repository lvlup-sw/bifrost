// =============================================================================
// <copyright file="CpqTuningResolverTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Queues;

namespace Bifrost.Tests.Queues;

/// <summary>
/// Maps each <see cref="CpqTuningProfile"/> (and the element reference-ness it depends on) to the
/// stickiness and buffer capacity the priority work queue applies.
/// </summary>
public sealed class CpqTuningResolverTests
{
    /// <summary>Balanced buffers reference-bearing elements at the baseline stickiness.</summary>
    [Test]
    public async Task Balanced_ReferenceElement_BuffersAtBaselineStickiness()
    {
        var (stickiness, bufferCapacity) = CpqTuningResolver.Resolve(CpqTuningProfile.Balanced, elementContainsReferences: true);

        await Assert.That(stickiness).IsEqualTo(1);
        await Assert.That(bufferCapacity).IsEqualTo(16);
    }

    /// <summary>Balanced leaves buffering off for pure value-type elements.</summary>
    [Test]
    public async Task Balanced_ValueElement_DisablesBuffering()
    {
        var (stickiness, bufferCapacity) = CpqTuningResolver.Resolve(CpqTuningProfile.Balanced, elementContainsReferences: false);

        await Assert.That(stickiness).IsEqualTo(1);
        await Assert.That(bufferCapacity).IsEqualTo(0);
    }

    /// <summary>LowConcurrency raises stickiness and turns buffering on.</summary>
    [Test]
    public async Task LowConcurrency_RaisesStickinessAndBuffers()
    {
        var (stickiness, bufferCapacity) = CpqTuningResolver.Resolve(CpqTuningProfile.LowConcurrency, elementContainsReferences: false);

        await Assert.That(stickiness).IsEqualTo(4);
        await Assert.That(bufferCapacity).IsEqualTo(16);
    }

    /// <summary>StrictOrdering keeps the baseline stickiness and leaves buffering off.</summary>
    [Test]
    public async Task StrictOrdering_BaselineStickinessWithoutBuffering()
    {
        var (stickiness, bufferCapacity) = CpqTuningResolver.Resolve(CpqTuningProfile.StrictOrdering, elementContainsReferences: true);

        await Assert.That(stickiness).IsEqualTo(1);
        await Assert.That(bufferCapacity).IsEqualTo(0);
    }
}
