// =============================================================================
// <copyright file="CpqTuningResolver.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

namespace Bifrost.Queues;

/// <summary>
/// Maps a <see cref="CpqTuningProfile"/> to the concrete stickiness factor and buffer capacity the
/// priority work queue hands its internal MultiQueue. The <see cref="CpqTuningProfile.Balanced"/> default
/// keys buffering off whether the element type carries managed references, so the buffering gain lands
/// automatically on reference-bearing work and stays out of the way for pure value-type work.
/// </summary>
internal static class CpqTuningResolver
{
    /// <summary>The buffer capacity used when buffering is on: the largest size the sub-queue buffers support.</summary>
    private const int BufferedCapacity = 16;

    /// <summary>The raised stickiness used in the low-concurrency profile.</summary>
    private const int LowConcurrencyStickiness = 4;

    /// <summary>
    /// Resolves a profile and the element's reference-ness to a <c>(stickiness, bufferCapacity)</c> pair.
    /// </summary>
    /// <param name="profile">The selected tuning profile.</param>
    /// <param name="elementContainsReferences">
    /// Whether the queue's element type is, or contains, a managed reference (the
    /// <see cref="System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences{T}"/> result).
    /// </param>
    /// <returns>The stickiness factor and buffer capacity to construct the queue with.</returns>
    internal static (int Stickiness, int BufferCapacity) Resolve(CpqTuningProfile profile, bool elementContainsReferences)
        => profile switch
        {
            CpqTuningProfile.LowConcurrency => (LowConcurrencyStickiness, BufferedCapacity),
            CpqTuningProfile.StrictOrdering => (1, 0),

            // Balanced (and any future-added value, defensively): baseline stickiness, with buffering
            // keyed off the element's reference-ness so reference-bearing work gets it and value-type
            // work does not.
            _ => (1, elementContainsReferences ? BufferedCapacity : 0),
        };
}
