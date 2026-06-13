// =============================================================================
// <copyright file="PaddedTopSlot.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================
// Ported from lvlup-sw/DataFerry@2bf0456 (src/DataFerry/Concurrency/MultiQueue/PaddedTopSlot.cs)

using System.Runtime.CompilerServices;

namespace Bifrost.Concurrency.MultiQueue;

/// <summary>
/// A single generic <typeparamref name="TPriority"/> slot isolated on its own 128-byte cache line
/// using a padded backing array (DR-2).
/// </summary>
/// <typeparam name="TPriority">The priority type cached as a sub-queue's "top" value.</typeparam>
/// <remarks>
/// <para>
/// The cached top priority is generic, and the CLR
/// forbids <see cref="System.Runtime.InteropServices.LayoutKind.Explicit"/> on generic types, so
/// the padding trick in <see cref="SubQueueHeader"/> cannot be reused here. Instead it exploits the
/// one layout guarantee the CLR <i>does</i> give for generics: the elements of a single-dimension
/// array are laid out contiguously in memory. By allocating a <typeparamref name="TPriority"/>[]
/// with a full cache line of padding elements on each side of one live slot, the live element is
/// guaranteed to sit on its own cache line, isolated from whatever objects neighbour the array.
/// </para>
/// <para>
/// <see cref="PadElementCount"/> is <c>ceil(128 / StoredElementSize)</c> so
/// that the padding on each side spans at least one full 128-byte cache line (the BCL's effective
/// false-sharing unit; see <see cref="SubQueueHeader"/> for why 128 and not 64).
/// <see cref="StoredElementSize"/> is the size of the element <i>as stored in the array</i>:
/// </para>
/// <list type="bullet">
/// <item>For a reference type, the array stores an object reference, which is
/// <see cref="IntPtr.Size"/> bytes (8 on x64) regardless of the referenced object's size.</item>
/// <item>For a value type, the element is the value itself, sized by
/// <see cref="Unsafe.SizeOf{T}"/>. This also correctly covers value types that <i>contain</i>
/// managed references (e.g. a struct with a string field), where the stored element is the whole
/// inlined struct, not a single pointer.</item>
/// </list>
/// <para>
/// The live slot is centered: <see cref="SlotIndex"/> equals <see cref="PadElementCount"/>, with
/// the same padding count on each side, giving a total backing length of
/// <c>(2 * PadElementCount) + 1</c>. Centering keeps the cache line clear of neighbours on
/// <i>both</i> sides of the array, not just one.
/// </para>
/// <para>
/// The layout metadata (<see cref="StoredElementSize"/>, <see cref="PadElementCount"/>,
/// <see cref="SlotIndex"/>) depends only on <typeparamref name="TPriority"/>, so it is computed
/// once per generic instantiation into static properties; instances carry only the backing array
/// reference, and the slot index folds to a JIT constant in <see cref="Get"/>/<see cref="Set"/>.
/// </para>
/// <para>
/// The accompanying <c>SubQueueHeaderTests.Layout_TopPriorityStorage_IsolatedOnOwnCacheLine</c>
/// asserts the per-side padding spans a full cache line, that the slot is centered, and that the
/// stored-element-size rule holds for value types (<see cref="long"/>, <see cref="Guid"/>) and
/// reference types (<see cref="string"/>).
/// </para>
/// </remarks>
internal readonly struct PaddedTopSlot<TPriority>
{
    /// <summary>The effective false-sharing unit in bytes, matching the BCL 128-byte padding.</summary>
    private const int CacheLineBytes = 128;

    private readonly TPriority[] _backing;

    /// <summary>
    /// Initializes a new instance of the <see cref="PaddedTopSlot{TPriority}"/> struct, allocating
    /// a padded backing array sized so the single live slot occupies its own cache line.
    /// </summary>
    public PaddedTopSlot() => _backing = new TPriority[(2 * PadElementCount) + 1];

    /// <summary>
    /// Gets the size, in bytes, of one array element as stored: <see cref="IntPtr.Size"/> for a
    /// reference type, otherwise <see cref="Unsafe.SizeOf{T}"/>. It tests
    /// <see cref="Type.IsValueType"/> rather than
    /// <see cref="RuntimeHelpers.IsReferenceOrContainsReferences{T}"/>: a value type that
    /// <i>contains</i> a reference (e.g. a struct wrapping a string) is still stored inline as the
    /// full struct, so its stored size is <see cref="Unsafe.SizeOf{T}"/>, not a single pointer.
    /// </summary>
    internal static int StoredElementSize { get; } =
        typeof(TPriority).IsValueType ? Unsafe.SizeOf<TPriority>() : IntPtr.Size;

    /// <summary>
    /// Gets the number of padding elements placed on <i>each</i> side of the live slot. Equal to
    /// <c>ceil(128 / StoredElementSize)</c>, guaranteeing at least a full cache line of padding
    /// per side.
    /// </summary>
    internal static int PadElementCount { get; } =
        (CacheLineBytes + StoredElementSize - 1) / StoredElementSize;

    /// <summary>
    /// Gets the index of the live slot within the backing array. Equal to
    /// <see cref="PadElementCount"/> (the slot is centered).
    /// </summary>
    internal static int SlotIndex => PadElementCount;

    /// <summary>Reads the current value of the live slot.</summary>
    /// <returns>The cached top priority.</returns>
    internal TPriority Get() => _backing[SlotIndex];

    /// <summary>Writes a value to the live slot.</summary>
    /// <param name="value">The cached top priority to store.</param>
    internal void Set(TPriority value) => _backing[SlotIndex] = value;
}
