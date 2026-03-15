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
    public async IAsyncEnumerable<DeadLetteredWork<TWork>> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        while (_channel.Reader.TryRead(out var item))
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Decrement(ref _count);
            yield return item;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
