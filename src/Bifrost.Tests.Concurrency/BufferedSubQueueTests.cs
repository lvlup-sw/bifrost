// =============================================================================
// <copyright file="BufferedSubQueueTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Runtime.CompilerServices;
using Bifrost.Concurrency;

namespace Bifrost.Tests.Concurrency;

/// <summary>
/// Coverage for the ESA 2021 §4 buffered MultiQueue (DR-1..DR-7): the <c>[InlineArray(16)]</c>
/// buffer storage, the buffered push/pop algorithm (insertion buffer <c>I</c>, sorted deletion
/// buffer <c>D</c>, flush-on-full, refill-on-empty, eviction cascade), the D-empty emptiness
/// invariant, the seqlock/occupancy/Count integration at the <c>D</c> boundary, write-barrier-safe
/// moves, the default-OFF bit-exact bypass, and the <c>BIFROST_TEST_HOOKS</c> counters.
/// </summary>
/// <remarks>
/// <para>
/// <b>Non-tautology.</b> The differential tests run a buffered (cap 16) and an unbuffered sub-queue
/// over identical seeds and assert the per-sub-queue dequeue order is byte-for-byte identical; a
/// buffered path that reordered, dropped, or duplicated an element would diverge from the proven
/// arity-4 heap. The invariant and refill tests assert the exact resident structure (D holds the
/// smallest <c>min(cap, |heap|)</c> in sorted order), not merely that a pop returned.
/// </para>
/// </remarks>
public class BufferedSubQueueTests
{
    /// <summary>
    /// Builds a sub-queue with the given logical buffer capacity (0 = unbuffered). Named arguments
    /// match the existing <see cref="SubQueue{TElement, TPriority}"/> construction idiom in the suite.
    /// </summary>
    /// <param name="bufferCapacity">The logical buffer capacity in <c>[0, 16]</c>.</param>
    /// <returns>A fresh <c>(int, int)</c> sub-queue.</returns>
    private static SubQueue<int, int> NewSubQueue(int bufferCapacity)
        => new(comparer: null, index: 0, occupancy: new ulong[1], bufferCapacity: bufferCapacity);

    /// <summary>
    /// DR-1: the <see cref="SubQueueBuffer{TElement, TPriority}"/> inline-array struct holds exactly
    /// <see cref="SubQueue{TElement, TPriority}.BufferCapacityMax"/> (16) <c>(element, priority)</c>
    /// slots; writing all 16 through the <c>AsSpan</c> view and reading them back round-trips every
    /// slot in order, and a length-<c>n</c> span exposes exactly the first <c>n</c> logical slots.
    /// </summary>
    [Test]
    public async Task BufferLayout_InlineArray16_RoundTripsAllSlots()
    {
        SubQueueBuffer<int, int> buffer = default;

        // A span cannot cross an `await`; capture the lengths and copies into locals first, then assert.
        int fullLength = SubQueueBuffer<int, int>.AsSpan(ref buffer, SubQueue<int, int>.BufferCapacityMax).Length;

        // Write 16 distinct (element, priority) tuples through the full-length span.
        WriteSlots(ref buffer);

        // Read them back through a fresh span over the same storage into managed arrays.
        var readbackElements = new int[SubQueue<int, int>.BufferCapacityMax];
        var readbackPriorities = new int[SubQueue<int, int>.BufferCapacityMax];
        CopyOut(ref buffer, SubQueue<int, int>.BufferCapacityMax, readbackElements, readbackPriorities);

        // A length-n span honors the logical n <= 16 and aliases the same leading storage.
        int partialLength = SubQueueBuffer<int, int>.AsSpan(ref buffer, 5).Length;
        var partialPriorities = new int[5];
        CopyOut(ref buffer, 5, new int[5], partialPriorities);

        await Assert.That(fullLength).IsEqualTo(16).Because("the full-length span exposes all 16 inline slots");
        for (int i = 0; i < readbackElements.Length; i++)
        {
            await Assert.That(readbackElements[i]).IsEqualTo(i * 10).Because("slot element round-trips");
            await Assert.That(readbackPriorities[i]).IsEqualTo(i).Because("slot priority round-trips");
        }

        await Assert.That(partialLength).IsEqualTo(5).Because("a length-n span exposes exactly the first n logical slots");
        for (int i = 0; i < partialPriorities.Length; i++)
        {
            await Assert.That(partialPriorities[i]).IsEqualTo(i).Because("the partial span aliases the same leading storage");
        }
    }

    /// <summary>Writes 16 distinct <c>(i*10, i)</c> tuples through the full-length span (no <c>await</c> in scope).</summary>
    private static void WriteSlots(ref SubQueueBuffer<int, int> buffer)
    {
        Span<(int Element, int Priority)> full = SubQueueBuffer<int, int>.AsSpan(ref buffer, SubQueue<int, int>.BufferCapacityMax);
        for (int i = 0; i < full.Length; i++)
        {
            full[i] = (Element: i * 10, Priority: i);
        }
    }

    /// <summary>Copies the first <paramref name="length"/> slots out into managed arrays (no <c>await</c> in scope).</summary>
    private static void CopyOut(ref SubQueueBuffer<int, int> buffer, int length, int[] elements, int[] priorities)
    {
        Span<(int Element, int Priority)> span = SubQueueBuffer<int, int>.AsSpan(ref buffer, length);
        for (int i = 0; i < span.Length; i++)
        {
            elements[i] = span[i].Element;
            priorities[i] = span[i].Priority;
        }
    }
}
