// =============================================================================
// <copyright file="SubQueueHeader.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Runtime.InteropServices;

namespace Bifrost.Concurrency;

/// <summary>
/// The padded, lock-free-read "hot" header for a single MultiQueue sub-queue.
/// </summary>
/// <remarks>
/// <para>
/// In the two-choice MultiQueue design, threads sample two random
/// sub-queues and read each one's hot mutable state <i>without taking that sub-queue's lock</i>
/// (striped <see cref="Count"/>, the seqlock <see cref="TopVersion"/>, and the <see cref="EmptyFlag"/>).
/// Because these fields are read concurrently by foreign threads while the owning thread mutates
/// them, two hot fields sharing a cache line would cause false sharing: a write to one
/// field invalidates the line in every other core's cache, serializing otherwise-independent
/// lock-free reads. To prevent that, each hot field is isolated on its own 128-byte cache line,
/// with padding <i>before</i> the first field and <i>after</i> the last so that neither a
/// preceding object header nor a trailing neighbour allocation can land on a hot field's line.
/// </para>
/// <para>
/// The 128-byte padding (rather than 64) mirrors the BCL's <c>PaddedHeadAndTail</c> pattern used
/// by <c>System.Collections.Concurrent.ConcurrentQueueSegment</c>. Although an x64 cache line is
/// 64 bytes, the adjacent-cache-line prefetcher fetches lines in 128-byte-aligned pairs, so the
/// runtime treats 128 bytes as the effective false-sharing unit. Padding to 128 bytes is the
/// conservative, BCL-proven choice.
/// </para>
/// <para>
/// <see cref="StructLayoutAttribute"/> with <see cref="LayoutKind.Explicit"/> is the only way to
/// pin fields to exact byte offsets, but the CLR forbids explicit layout on generic types. The
/// cached top <i>priority</i> is a generic <c>TPriority</c>, so it cannot live here. It is stored
/// separately by <see cref="PaddedTopSlot{TPriority}"/>, which achieves the same cache-line
/// isolation using a padded array instead of explicit offsets. This struct therefore holds only
/// the non-generic hot words; the generic top is isolated elsewhere.
/// </para>
/// <para>
/// The cold fields (the heap array reference and heap size) live elsewhere: they are only ever
/// touched under the sub-queue lock, are never sampled lock-free, and so do not belong in this
/// padded header. Keeping them out avoids wasting cache lines on fields that never contend.
/// </para>
/// <para>
/// <b>Layout (x64).</b> Total <see cref="StructLayoutAttribute.Size"/> is 512 bytes = four 128-byte lines:
/// </para>
/// <list type="bullet">
/// <item>bytes [0,128)   : leading pad (isolates the first hot field from any preceding neighbour).</item>
/// <item>bytes [128,256) : <see cref="Count"/> on its own line.</item>
/// <item>bytes [256,384) : <see cref="TopVersion"/> on its own line.</item>
/// <item>bytes [384,512) : <see cref="EmptyFlag"/> on its own line; the remainder of this line is
/// the trailing pad isolating the last hot field from any following neighbour.</item>
/// </list>
/// <para>
/// The accompanying <c>SubQueueHeaderTests.Layout_HotFields_SeparatedFromColdFieldsBy128Bytes</c>
/// encodes these invariants as runtime assertions so a refactor that accidentally collapses two
/// hot fields onto one line fails the build.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 512)]
internal struct SubQueueHeader
{
    /// <summary>
    /// The striped element count for this sub-queue, read lock-free during two-choice sampling.
    /// Placed on its own 128-byte cache line (offset 128) with a full line of leading padding.
    /// </summary>
    [FieldOffset(128)]
    internal int Count;

    /// <summary>
    /// The seqlock version stamp guarding the cached top. Even = stable, odd = a writer is
    /// mid-update; readers retry while it is odd or changed across a read. Isolated on its own
    /// 128-byte cache line (offset 256).
    /// </summary>
    [FieldOffset(256)]
    internal uint TopVersion;

    /// <summary>
    /// A non-zero value indicates the sub-queue is empty, letting samplers skip it without
    /// touching the heap. Isolated on its own 128-byte cache line (offset 384); the remaining
    /// bytes up to <see cref="StructLayoutAttribute.Size"/> (512) form the trailing pad.
    /// </summary>
    [FieldOffset(384)]
    internal int EmptyFlag;
}
