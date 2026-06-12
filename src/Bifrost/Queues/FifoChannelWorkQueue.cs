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
/// <see cref="EnqueueAsync(T, CancellationToken)"/> and <see cref="Complete"/> are
/// concrete-only members — they are not part of <see cref="IWorkQueue{T}"/>. The
/// orchestrator reaches them via the concrete type (pattern-matched), keeping the
/// strategy contract minimal for bindings that have no waiting accept path.
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
    public async ValueTask<bool> WaitToDequeueAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Contract shutdown semantic: cancellation completes the wait with false.
            return false;
        }
        catch (ChannelClosedException)
        {
            // Defensive: WaitToReadAsync reports completion as false, but normalize anyway.
            return false;
        }
    }

    /// <inheritdoc/>
    public bool TryDequeue([MaybeNullWhen(false)] out T item) => _channel.Reader.TryRead(out item);

    /// <summary>
    /// Asynchronously enqueues an item, waiting for space when the queue is at capacity
    /// (producer-wait semantics, concrete-only member).
    /// </summary>
    /// <param name="item">The item to enqueue.</param>
    /// <param name="cancellationToken">Token whose cancellation abandons the wait.</param>
    /// <returns>
    /// <c>true</c> when the item was accepted; <c>false</c> when the queue was completed
    /// via <see cref="Complete"/> or <paramref name="cancellationToken"/> was cancelled
    /// before space became available. Never throws for either condition.
    /// </returns>
    public async ValueTask<bool> EnqueueAsync(T item, CancellationToken cancellationToken)
    {
        try
        {
            await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Marks the queue as complete for adding (concrete-only member). Residual items
    /// remain dequeueable via <see cref="TryDequeue(out T)"/>; once drained,
    /// <see cref="WaitToDequeueAsync(CancellationToken)"/> completes <c>false</c>.
    /// </summary>
    public void Complete() => _ = _channel.Writer.TryComplete();
}
