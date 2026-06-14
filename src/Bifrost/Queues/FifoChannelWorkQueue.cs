// =============================================================================
// <copyright file="FifoChannelWorkQueue.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace Bifrost.Queues;

/// <summary>
/// The default <see cref="IWorkQueue{T}"/> binding: a strict-FIFO queue backed by a
/// bounded <see cref="Channel{T}"/>.
/// </summary>
/// <typeparam name="T">The type of item held by the queue.</typeparam>
/// <remarks>
/// <para>
/// This binding preserves the orchestrator's pre-existing channel semantics byte-for-byte:
/// strict FIFO ordering and producer-wait on the asynchronous accept path
/// (<see cref="BoundedChannelFullMode.Wait"/>). Priority bindings deliberately do not
/// provide the FIFO guarantee.
/// </para>
/// <para>
/// <see cref="WriteAsync(T, CancellationToken)"/> and <see cref="Complete"/> are
/// concrete-only members — they are not part of <see cref="IWorkQueue{T}"/>. The
/// orchestrator reaches them via the concrete type (pattern-matched), keeping the
/// strategy contract minimal for bindings that have no waiting accept path.
/// </para>
/// <para>
/// <b>DR-7 (no FIFO regression):</b> both asynchronous members forward the channel's
/// own <see cref="ValueTask"/>s directly, with no wrapper <c>async</c> state machine.
/// A suspending wait therefore rides the channel's pooled
/// <see cref="System.Threading.Tasks.Sources.IValueTaskSource"/> exactly as the
/// pre-rewrite <c>ReadAllAsync</c> loop did. The cost of this shape is that the
/// channel's native fault semantics surface to callers: cancellation appears as
/// <see cref="OperationCanceledException"/> (permitted by the
/// <see cref="IWorkQueue{T}"/> contract; the orchestrator's canonical-loop boundary
/// absorbs it) and post-completion writes fault with
/// <see cref="ChannelClosedException"/> (mapped by the orchestrator's single
/// producer-wait async layer).
/// </para>
/// </remarks>
internal sealed class FifoChannelWorkQueue<T> : IWorkQueue<T>
{
    private readonly Channel<T> _channel;

    /// <summary>
    /// Initializes a new instance of the <see cref="FifoChannelWorkQueue{T}"/> class.
    /// </summary>
    /// <param name="capacity">The bounded capacity of the queue.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="capacity"/> is less than one.
    /// </exception>
    public FifoChannelWorkQueue(int capacity)
    {
        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Bounded channels support counting, so this is the channel's exact item count;
    /// under concurrency it can still lag in-flight operations, per the contract.
    /// </remarks>
    public int Count => _channel.Reader.Count;

    /// <inheritdoc/>
    public bool TryEnqueue(in T item) => _channel.Writer.TryWrite(item);

    /// <inheritdoc/>
    /// <remarks>
    /// Direct forward of <see cref="ChannelReader{T}.WaitToReadAsync(CancellationToken)"/>
    /// — no wrapper state machine, so a suspending wait stays allocation-free on the
    /// channel's pooled source (DR-7). Completion-and-drained surfaces as <c>false</c>;
    /// cancellation surfaces as <see cref="OperationCanceledException"/>, which the
    /// contract permits and the canonical consume loop's boundary absorbs.
    /// </remarks>
    public ValueTask<bool> WaitToDequeueAsync(CancellationToken cancellationToken)
        => _channel.Reader.WaitToReadAsync(cancellationToken);

    /// <inheritdoc/>
    public bool TryDequeue([MaybeNullWhen(false)] out T item) => _channel.Reader.TryRead(out item);

    /// <summary>
    /// Asynchronously enqueues an item, waiting for space when the queue is at capacity
    /// (producer-wait semantics, concrete-only member). Direct forward of
    /// <see cref="ChannelWriter{T}.WriteAsync(T, CancellationToken)"/> — no wrapper
    /// state machine (DR-7) — so the channel's native fault semantics surface here:
    /// cancellation faults the task with <see cref="OperationCanceledException"/> and
    /// a post-<see cref="Complete"/> write faults with
    /// <see cref="ChannelClosedException"/>. The orchestrator's single producer-wait
    /// async layer owns the mapping of both to its rejected-shutdown outcome.
    /// </summary>
    /// <param name="item">The item to enqueue.</param>
    /// <param name="cancellationToken">Token whose cancellation abandons the wait.</param>
    /// <returns>A task that completes when the item has been accepted.</returns>
    public ValueTask WriteAsync(T item, CancellationToken cancellationToken)
        => _channel.Writer.WriteAsync(item, cancellationToken);

    /// <summary>
    /// Marks the queue as complete for adding (concrete-only member). Residual items
    /// remain dequeueable via <see cref="TryDequeue(out T)"/>; once drained,
    /// <see cref="WaitToDequeueAsync(CancellationToken)"/> completes <c>false</c>.
    /// </summary>
    public void Complete() => _ = _channel.Writer.TryComplete();
}
