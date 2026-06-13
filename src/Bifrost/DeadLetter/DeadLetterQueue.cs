// =============================================================================
// <copyright file="DeadLetterQueue.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Threading.Channels;

using Bifrost.Core.DeadLetter;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bifrost.DeadLetter;

/// <summary>
/// Channel-backed implementation of <see cref="IDeadLetterQueue{TWork}"/>.
/// </summary>
/// <typeparam name="TWork">The type of work item.</typeparam>
/// <remarks>
/// Uses a bounded channel with <see cref="BoundedChannelFullMode.DropOldest"/>
/// to prevent unbounded memory growth. When the queue is full, the oldest
/// item is dropped and a warning is logged. The <see cref="DroppedCount"/> property
/// tracks the total number of items dropped.
/// </remarks>
internal sealed class DeadLetterQueue<TWork> : IDeadLetterQueue<TWork>
{
    private readonly Channel<DeadLetteredWork<TWork>> _channel;
    private readonly ILogger<DeadLetterQueue<TWork>> _logger;
    private readonly int _capacity;
    private int _count;
    private long _droppedCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeadLetterQueue{TWork}"/> class.
    /// </summary>
    /// <param name="options">The dead letter queue options.</param>
    /// <param name="logger">The logger instance.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> or <paramref name="logger"/> is null.</exception>
    public DeadLetterQueue(IOptions<DeadLetterQueueOptions> options, ILogger<DeadLetterQueue<TWork>> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _capacity = options.Value.Capacity;

        _channel = Channel.CreateBounded<DeadLetteredWork<TWork>>(
            new BoundedChannelOptions(_capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false,
            });
    }

    /// <inheritdoc/>
    public int Count => Volatile.Read(ref _count);

    /// <summary>
    /// Gets the total number of items that have been dropped due to the queue being at capacity.
    /// </summary>
    public long DroppedCount => Volatile.Read(ref _droppedCount);

    /// <inheritdoc/>
    public ValueTask EnqueueAsync(DeadLetteredWork<TWork> item, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (_channel.Reader.CanCount && _channel.Reader.Count >= _capacity)
        {
            Interlocked.Increment(ref _droppedCount);
            _logger.LogWarning("DLQ at capacity ({Capacity}), oldest item will be dropped", _capacity);
        }

        if (_channel.Writer.TryWrite(item))
        {
            // Only increment if we haven't exceeded capacity.
            // With DropOldest, TryWrite always succeeds, but the channel
            // may have dropped an old item. We need to track actual count.
            if (_channel.Reader.CanCount)
            {
                // Use the channel's actual count for accuracy
                Volatile.Write(ref _count, _channel.Reader.Count);
            }
            else
            {
                Interlocked.Increment(ref _count);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Yields the items buffered in the queue at the moment of enumeration and then completes;
    /// it never waits for items that have not yet been enqueued, so the drain runs synchronously
    /// and is surfaced through a lightweight enumerator rather than an <c>async</c> state machine.
    /// Each item is removed (and <see cref="Count"/> decremented) as it is yielded, so abandoning
    /// the enumeration early leaves the remaining items in the queue.
    /// </remarks>
    public IAsyncEnumerable<DeadLetteredWork<TWork>> ReadAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return new DrainEnumerable(this, ct);
    }

    /// <summary>
    /// Exposes a non-blocking, synchronous drain of the queue's currently-buffered items as an
    /// <see cref="IAsyncEnumerable{T}"/>. Nothing here awaits: each step completes synchronously
    /// because <see cref="ChannelReader{T}.TryRead(out T)"/> never waits for a future item.
    /// </summary>
    private sealed class DrainEnumerable : IAsyncEnumerable<DeadLetteredWork<TWork>>
    {
        private readonly DeadLetterQueue<TWork> _queue;
        private readonly CancellationToken _ct;

        public DrainEnumerable(DeadLetterQueue<TWork> queue, CancellationToken ct)
        {
            _queue = queue;
            _ct = ct;
        }

        public IAsyncEnumerator<DeadLetteredWork<TWork>> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new DrainEnumerator(_queue, cancellationToken.CanBeCanceled ? cancellationToken : _ct);
    }

    /// <summary>
    /// Reads the channel with <see cref="ChannelReader{T}.TryRead(out T)"/>, decrementing the
    /// tracked count per item and completing as soon as the channel is empty. Every
    /// <see cref="MoveNextAsync"/> returns an already-completed <see cref="ValueTask{TResult}"/>.
    /// </summary>
    private sealed class DrainEnumerator : IAsyncEnumerator<DeadLetteredWork<TWork>>
    {
        private readonly DeadLetterQueue<TWork> _queue;
        private readonly CancellationToken _ct;

        public DrainEnumerator(DeadLetterQueue<TWork> queue, CancellationToken ct)
        {
            _queue = queue;
            _ct = ct;
        }

        public DeadLetteredWork<TWork> Current { get; private set; } = default!;

        public ValueTask<bool> MoveNextAsync()
        {
            _ct.ThrowIfCancellationRequested();

            if (_queue._channel.Reader.TryRead(out DeadLetteredWork<TWork> item))
            {
                Interlocked.Decrement(ref _queue._count);
                Current = item;
                return new ValueTask<bool>(true);
            }

            Current = default!;
            return new ValueTask<bool>(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
