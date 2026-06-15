// =============================================================================
// <copyright file="SubQueueBuffer.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Bifrost.Concurrency;

/// <summary>
/// Compile-time-fixed inline storage for one ESA 2021 §4 buffer (the unsorted insertion buffer
/// <c>I</c> or the sorted deletion buffer <c>D</c>) of a <see cref="SubQueue{TElement, TPriority}"/>.
/// A C# 12 <see cref="InlineArrayAttribute"/> struct holding exactly
/// <see cref="SubQueue{TElement, TPriority}.BufferCapacityMax"/> (16) <c>(element, priority)</c>
/// slots inline, the safe-code equivalent of the C++ reference's
/// <c>std::array&lt;value_type, 16&gt;</c>.
/// </summary>
/// <typeparam name="TElement">The element type stored alongside each priority.</typeparam>
/// <typeparam name="TPriority">The priority type ordered by the queue's comparer.</typeparam>
/// <remarks>
/// <para>
/// Inline arrays are the idiomatic modern .NET replacement for unmanaged <c>fixed</c> buffers and,
/// unlike them, are fully GC-tracked even when the element type contains managed references, so the
/// reference-containing instantiations (e.g. <c>(string, string)</c>, <c>(object, long)</c>) are
/// safe. The single instance field plus the <c>InlineArray(16)</c> attribute is the entire layout;
/// no explicit <see cref="StructLayoutAttribute"/> is applied (the attribute is incompatible with
/// inline arrays, which control their own layout). The storage lives inline in the owning sub-queue
/// object — no extra heap object and no pointer-chase — which is the whole locality point of the
/// buffered design.
/// </para>
/// <para>
/// Because the buffers are mutated and read only by the holder of the sub-queue's
/// <see cref="SubQueue{TElement, TPriority}.SyncLock"/>, they add no new cross-thread sharing; the
/// lock-free published top stays the only lock-free reader surface, so the false-sharing / padding
/// story of the sub-queue is unchanged.
/// </para>
/// </remarks>
[InlineArray(SubQueue<TElement, TPriority>.BufferCapacityMax)]
internal struct SubQueueBuffer<TElement, TPriority>
{
    /// <summary>
    /// The first (and only declared) slot. The <see cref="InlineArrayAttribute"/> repeats this field's
    /// storage <see cref="SubQueue{TElement, TPriority}.BufferCapacityMax"/> times, giving 16 contiguous
    /// <c>(element, priority)</c> slots addressable through the language indexer or <see cref="AsSpan"/>.
    /// </summary>
    private (TElement Element, TPriority Priority) _element0;

    /// <summary>
    /// Creates a <see cref="Span{T}"/> view over the first <paramref name="length"/> inline slots of
    /// <paramref name="buffer"/>. All buffer moves operate through this span so they lower to the
    /// runtime's barrier-correct <see cref="Span{T}.CopyTo(Span{T})"/> / <see cref="Span{T}.Clear"/>
    /// (a barrier-free <c>memmove</c> for value-type-only tuples).
    /// </summary>
    /// <param name="buffer">The inline buffer to view; passed by reference so the span aliases its storage.</param>
    /// <param name="length">
    /// The number of leading slots to expose; must be in <c>[0, BufferCapacityMax]</c>. Callers pass
    /// the buffer's logical length, never more than the fixed inline capacity.
    /// </param>
    /// <returns>A span aliasing the first <paramref name="length"/> slots of <paramref name="buffer"/>.</returns>
    internal static Span<(TElement Element, TPriority Priority)> AsSpan(
        ref SubQueueBuffer<TElement, TPriority> buffer,
        int length)
    {
        // Defense-in-depth: CreateSpan does no bounds check, so a length past the fixed inline
        // capacity would silently fabricate an out-of-bounds span over adjacent storage (a
        // memory-safety hole). All production callers are provably bounded by the logical
        // bufferCapacity (≤ BufferCapacityMax); this debug-only assert fast-fails any future
        // caller that violates the precondition, at zero release cost.
        Debug.Assert(
            (uint)length <= SubQueue<TElement, TPriority>.BufferCapacityMax,
            "SubQueueBuffer.AsSpan length exceeds the fixed inline capacity.");

        return MemoryMarshal.CreateSpan(
            ref Unsafe.As<SubQueueBuffer<TElement, TPriority>, (TElement Element, TPriority Priority)>(ref buffer),
            length);
    }
}
