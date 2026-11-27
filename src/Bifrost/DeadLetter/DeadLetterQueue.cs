// =============================================================================
// <copyright file="DeadLetterQueue.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Threading.Channels;

using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.Outcomes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bifrost.DeadLetter;

/// <summary>
/// Channel-backed implementation of <see cref="IDeadLetterQueue{TWork}"/>.
/// </summary>
/// <typeparam name="TWork">The type of work item.</typeparam>
/// <remarks>
/// <para>
/// Uses bounded channels with <see cref="BoundedChannelFullMode.DropOldest"/>
/// to prevent unbounded memory growth. When a channel is full, its oldest
/// item is dropped and a warning is logged. The <see cref="DroppedCount"/> property
/// tracks the total number of items dropped.
/// </para>
/// <para>
/// Handler failures and admission rejections are kept in separate channels, each bounded by
/// <see cref="DeadLetterQueueOptions.Capacity"/>. An admission rejection is an entry with
/// <see cref="DeadLetteredWork{TWork}.AttemptCount"/> 0 and a <see cref="WorkRejectedException"/>;
/// a handler failure always has at least one attempt. Rejections are produced at the rate
/// producers submit work, so a rejection can only displace an older rejection and never a
/// handler failure. <see cref="ReadAllAsync"/> yields both kinds in arrival order.
/// </para>
/// </remarks>
internal sealed class DeadLetterQueue<TWork> : IDeadLetterQueue<TWork>
{
    private readonly Channel<Entry> _failures;
    private readonly Channel<Entry> _rejections;
    private readonly ILogger<DeadLetterQueue<TWork>> _logger;
    private readonly TerminalOutcomeCounters<TWork>? _counters;
    private readonly int _capacity;
    private long _droppedCount;
    private long _sequence;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeadLetterQueue{TWork}"/> class.
    /// </summary>
    /// <param name="options">The dead letter queue options.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="counters">Optional shared counters that also count each dropped item as an eviction.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> or <paramref name="logger"/> is null.</exception>
    public DeadLetterQueue(IOptions<DeadLetterQueueOptions> options, ILogger<DeadLetterQueue<TWork>> logger,
        TerminalOutcomeCounters<TWork>? counters = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _counters = counters;
        _capacity = options.Value.Capacity;
        _failures = CreateChannel(_ => OnDropped(rejection: false));
        _rejections = CreateChannel(_ => OnDropped(rejection: true));
    }

    /// <inheritdoc/>
    public int Count => _failures.Reader.Count + _rejections.Reader.Count;

    /// <summary>
    /// Gets the number of buffered handler failures, excluding admission rejections.
    /// </summary>
    internal int FailureCount => _failures.Reader.Count;

    /// <summary>
    /// Gets the total number of items that have been dropped due to the queue being at capacity.
    /// </summary>
    public long DroppedCount => Volatile.Read(ref _droppedCount);

    /// <inheritdoc/>
    public ValueTask EnqueueAsync(DeadLetteredWork<TWork> item, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var channel = IsAdmissionRejection(item) ? _rejections : _failures;
        channel.Writer.TryWrite(new Entry(Interlocked.Increment(ref _sequence), item));

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

    private static bool IsAdmissionRejection(DeadLetteredWork<TWork> item)
        => item.AttemptCount == 0 && item.Exception is WorkRejectedException;

    private Channel<Entry> CreateChannel(Action<Entry> itemDropped)
        => Channel.CreateBounded(
            new BoundedChannelOptions(_capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false,
            },
            itemDropped);

    private void OnDropped(bool rejection)
    {
        Interlocked.Increment(ref _droppedCount);
        _counters?.RecordEviction();
        if (rejection)
        {
            _logger.LogWarning("DLQ admission rejections at capacity ({Capacity}), oldest rejection will be dropped", _capacity);
        }
        else
        {
            _logger.LogWarning("DLQ at capacity ({Capacity}), oldest item will be dropped", _capacity);
        }
    }

    /// <summary>
    /// Takes the oldest buffered item across both channels.
    /// </summary>
    /// <param name="item">The item taken, when one was buffered.</param>
    /// <returns><see langword="true"/> when an item was taken.</returns>
    /// <remarks>
    /// A concurrent reader can take the peeked item first; the loop then peeks again until both
    /// channels are empty.
    /// </remarks>
    private bool TryTake(out DeadLetteredWork<TWork> item)
    {
        while (true)
        {
            var hasFailure = _failures.Reader.TryPeek(out var failure);
            var hasRejection = _rejections.Reader.TryPeek(out var rejection);
            if (!hasFailure && !hasRejection)
            {
                item = default!;
                return false;
            }

            var reader = hasFailure && (!hasRejection || failure.Sequence < rejection.Sequence)
                ? _failures.Reader
                : _rejections.Reader;
            if (reader.TryRead(out var entry))
            {
                item = entry.Item;
                return true;
            }
        }
    }

    /// <summary>A buffered item with its arrival sequence.</summary>
    /// <param name="Sequence">The arrival order across both channels.</param>
    /// <param name="Item">The dead-lettered work item.</param>
    private readonly record struct Entry(long Sequence, DeadLetteredWork<TWork> Item);

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
            => new DrainEnumerator(_queue, _ct, cancellationToken);
    }

    /// <summary>
    /// Reads the channels with <see cref="ChannelReader{T}.TryRead(out T)"/>, decrementing the
    /// tracked count per item and completing as soon as both channels are empty. Every
    /// <see cref="MoveNextAsync"/> returns an already-completed <see cref="ValueTask{TResult}"/>.
    /// </summary>
    private sealed class DrainEnumerator : IAsyncEnumerator<DeadLetteredWork<TWork>>
    {
        private readonly DeadLetterQueue<TWork> _queue;
        private readonly CancellationToken _ct;

        private readonly CancellationToken _enumerationToken;

        public DrainEnumerator(DeadLetterQueue<TWork> queue, CancellationToken ct, CancellationToken enumerationToken)
        {
            _queue = queue;
            _ct = ct;
            _enumerationToken = enumerationToken;
        }

        public DeadLetteredWork<TWork> Current { get; private set; } = default!;

        public ValueTask<bool> MoveNextAsync()
        {
            _ct.ThrowIfCancellationRequested();
            _enumerationToken.ThrowIfCancellationRequested();

            if (_queue.TryTake(out DeadLetteredWork<TWork> item))
            {
                Current = item;
                return new ValueTask<bool>(true);
            }

            Current = default!;
            return new ValueTask<bool>(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
