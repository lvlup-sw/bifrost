// =============================================================================
// <copyright file="WorkOrchestrator.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Threading.Channels;

using Bifrost.Core;
using Bifrost.Queues;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bifrost;

/// <summary>
/// Orchestrates work processing through an <see cref="IWorkQueue{T}"/> with
/// configurable workers.
/// </summary>
/// <typeparam name="TWork">The type of work item to process.</typeparam>
/// <remarks>
/// <para>
/// This implementation provides a high-performance, zero-allocation hot path for
/// enqueueing work items. It manages a pool of workers that consume items from the
/// internal work queue via the canonical wait/try-dequeue loop defined by the
/// <see cref="IWorkQueue{T}"/> contract. The binding is selected by
/// <see cref="WorkOrchestratorOptions.DispatchStrategy"/> via a direct
/// enum-switch factory in the constructor (DR-4) — no reflective resolution, so
/// the factory is trim/AOT-safe; all bindings are sealed, so the default FIFO
/// path devirtualizes (the FIFO wait-path is reached through an exact-type
/// pattern match, and interface calls on sealed bindings are candidates for
/// guarded devirtualization).
/// </para>
/// <para>
/// Work items are carried internally as <see cref="WorkEnvelope{TWork}"/> values
/// pairing the item with its <see cref="WorkClass"/> and a monotonic enqueue
/// timestamp. Admission outcomes surface as <see cref="EnqueueResult"/> values:
/// shutdown and cancellation are reported as
/// <see cref="RejectionReason.Shutdown"/> rejections, never as exceptions.
/// </para>
/// </remarks>
public sealed class WorkOrchestrator<TWork> : IWorkOrchestrator<TWork>
{
    private readonly IWorkQueue<WorkEnvelope<TWork>> _queue;
    private readonly IWorkHandler<TWork> _handler;
    private readonly ILogger<WorkOrchestrator<TWork>> _logger;
    private readonly Task[] _workers;
    private readonly CancellationTokenSource _cts = new();
    private readonly int _capacity;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TWork, WorkClass>? _classifier;

    /// <summary>
    /// Whether the queue has been completed for adding (drain, stop, or dispose).
    /// Volatile: read on the fail-fast enqueue path to map post-completion
    /// rejections to <see cref="RejectionReason.Shutdown"/>; the bindings' own
    /// try-enqueue surface cannot distinguish completion from capacity.
    /// </summary>
    private volatile bool _queueCompleted;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkOrchestrator{TWork}"/> class.
    /// </summary>
    /// <param name="handler">The handler that processes work items.</param>
    /// <param name="options">Configuration options for the orchestrator.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="timeProvider">
    /// The time provider used for enqueue timestamping; defaults to
    /// <see cref="TimeProvider.System"/> when null.
    /// </param>
    /// <param name="classifier">
    /// Optional work classifier (DR-2), the options-level alternative to per-call
    /// tagging. Precedence rule: a per-call class other than
    /// <see cref="WorkClass.Default"/> wins; a per-call
    /// <see cref="WorkClass.Default"/> defers to the classifier; with no classifier
    /// the class stays <see cref="WorkClass.Default"/>. The delegate is invoked only
    /// when the per-call class is <see cref="WorkClass.Default"/>, keeping the
    /// default FIFO hot path allocation-free.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when handler, options, or logger is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <see cref="WorkOrchestratorOptions.DispatchStrategy"/> is not a
    /// defined <see cref="DispatchStrategy"/> value.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown by the priority bindings when the admission watermarks in
    /// <see cref="WorkOrchestratorOptions.Priority"/> are not monotone
    /// non-decreasing with class urgency (DR-6).
    /// </exception>
    public WorkOrchestrator(
        IWorkHandler<TWork> handler,
        IOptions<WorkOrchestratorOptions> options,
        ILogger<WorkOrchestrator<TWork>> logger,
        TimeProvider? timeProvider = null,
        Func<TWork, WorkClass>? classifier = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _handler = handler;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _classifier = classifier;

        var opts = options.Value;
        _capacity = opts.Capacity;

        // DR-4: enum/factory-based strategy selection — a direct switch constructing
        // the sealed binding, no reflective resolution (trim/AOT-safe).
        _queue = opts.DispatchStrategy switch
        {
            DispatchStrategy.Fifo =>
                new FifoChannelWorkQueue<WorkEnvelope<TWork>>(opts.Capacity),
            DispatchStrategy.PriorityMultiQueue =>
                new ConcurrentPriorityWorkQueue<TWork>(opts.Capacity, opts.Priority, _timeProvider.TimestampFrequency),
            DispatchStrategy.PriorityLocking =>
                new LockingPriorityWorkQueue<TWork>(opts.Capacity, opts.Priority, _timeProvider.TimestampFrequency),
            _ => throw new ArgumentOutOfRangeException(
                nameof(options), opts.DispatchStrategy, "Unknown dispatch strategy."),
        };

        // Start worker tasks
        _workers = Enumerable.Range(0, opts.WorkerCount)
            .Select(i => Task.Factory.StartNew(
                () => WorkerLoopAsync($"Worker-{i}", _cts.Token),
                _cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap())
            .ToArray();
    }

    /// <inheritdoc/>
    public int PendingCount => _queue.Count;

    /// <inheritdoc/>
    public int ActiveWorkers => _workers.Length;

    /// <inheritdoc/>
    public int Capacity => _capacity;

    /// <summary>
    /// Gets the internal work queue as its <see cref="IWorkQueue{T}"/> strategy
    /// contract. Test-only inspection seam (via InternalsVisibleTo); the concrete
    /// binding is selected by <see cref="WorkOrchestratorOptions.DispatchStrategy"/>
    /// in the constructor (DR-4).
    /// </summary>
    internal IWorkQueue<WorkEnvelope<TWork>> WorkQueue => _queue;

    /// <summary>
    /// Gets or sets the queue-wait observation hook, invoked once per dequeued
    /// envelope with the envelope's <see cref="WorkClass"/> and the time it spent
    /// queued, computed via <see cref="TimeProvider.GetElapsedTime(long)"/> from
    /// <see cref="WorkEnvelope{TWork}.EnqueuedAtTicks"/>. Allocation-free when null;
    /// OpenTelemetry instrumentation attaches here.
    /// </summary>
    internal Action<WorkClass, TimeSpan>? QueueWaitObserved { get; set; }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>Enqueue semantics differ by strategy (DR-6), by design:</b>
    /// </para>
    /// <list type="table">
    ///   <listheader>
    ///     <term>Strategy</term>
    ///     <description>Behavior at capacity / under policy</description>
    ///   </listheader>
    ///   <item>
    ///     <term><see cref="DispatchStrategy.Fifo"/> (default)</term>
    ///     <description>
    ///     Producer-wait: awaits space when the queue is full, then
    ///     <see cref="EnqueueResult.Accepted"/>. Rejects only with
    ///     <see cref="RejectionReason.Shutdown"/> — on queue completion or
    ///     cancellation of <c>ct</c>. Never rejects for capacity.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term>
    ///     <see cref="DispatchStrategy.PriorityMultiQueue"/> /
    ///     <see cref="DispatchStrategy.PriorityLocking"/>
    ///     </term>
    ///     <description>
    ///     FAIL-FAST admission: never waits for space — the returned
    ///     <see cref="ValueTask{T}"/> is already completed. A try-enqueue failure
    ///     maps immediately to <see cref="RejectionReason.CapacityExceeded"/>;
    ///     class-watermark shedding and hard capacity exhaustion are deliberately
    ///     indistinguishable at this surface (rejections route to the dead-letter
    ///     queue when configured). After the queue is completed, rejections carry
    ///     <see cref="RejectionReason.Shutdown"/>. Producer-wait at capacity was
    ///     rejected in design as admission-side priority inversion: a blocked
    ///     producer queue-jumps whatever class drains first.
    ///     </description>
    ///   </item>
    /// </list>
    /// <para>
    /// <b>Work-class precedence (DR-2), shared by all enqueue overloads:</b> a
    /// per-call class other than <see cref="WorkClass.Default"/> wins; a per-call
    /// <see cref="WorkClass.Default"/> defers to the classifier; with no classifier
    /// the class stays <see cref="WorkClass.Default"/>.
    /// </para>
    /// </remarks>
    public ValueTask<EnqueueResult> EnqueueAsync(TWork work, WorkClass workClass = WorkClass.Default, CancellationToken ct = default)
    {
        var envelope = new WorkEnvelope<TWork>(work, ResolveClass(work, workClass), _timeProvider.GetTimestamp());

        // Default FIFO strategy on the concrete sealed binding (the exact-type pattern
        // match devirtualizes the calls, DR-4).
        if (_queue is FifoChannelWorkQueue<WorkEnvelope<TWork>> fifo)
        {
            if (ct.IsCancellationRequested)
            {
                // Matches the channel's own WriteAsync ordering: a cancelled token is
                // reported before admission is attempted — as Rejected(Shutdown),
                // never as an exception.
                return new ValueTask<EnqueueResult>(EnqueueResult.Rejected(RejectionReason.Shutdown));
            }

            // Try-write-first fast path (DR-7): while space is available, admission
            // completes fully synchronously — zero allocation, no async layer. The
            // bounded channel hands freed slots to parked writers before a try-write
            // can see them, so this cannot jump the producer-wait queue.
            if (fifo.TryEnqueue(envelope))
            {
                return new ValueTask<EnqueueResult>(EnqueueResult.Accepted);
            }

            // Queue full or completed: the single async layer on the enqueue path.
            return EnqueueFifoSlowAsync(fifo, envelope, ct);
        }

        // Priority strategies: fail-fast admission (DR-6) — fully synchronous, the
        // ValueTask below is always already completed.
        if (_queueCompleted || ct.IsCancellationRequested)
        {
            return new ValueTask<EnqueueResult>(EnqueueResult.Rejected(RejectionReason.Shutdown));
        }

        return new ValueTask<EnqueueResult>(
            _queue.TryEnqueue(envelope)
                ? EnqueueResult.Accepted
                : EnqueueResult.Rejected(RejectionReason.CapacityExceeded));
    }

    /// <inheritdoc/>
    public bool TryEnqueue(TWork work, WorkClass workClass = WorkClass.Default)
    {
        return _queue.TryEnqueue(new WorkEnvelope<TWork>(work, ResolveClass(work, workClass), _timeProvider.GetTimestamp()));
    }

    /// <inheritdoc/>
    public void Run(TWork work, WorkClass workClass = WorkClass.Default)
    {
        if (!TryEnqueue(work, workClass))
        {
            throw new InvalidOperationException("Queue is full");
        }
    }

    /// <inheritdoc/>
    public bool TryRun(TWork work, WorkClass workClass = WorkClass.Default)
    {
        return TryEnqueue(work, workClass);
    }

    /// <inheritdoc/>
    public Func<string, CancellationToken, Task> CreateWorkerFunction()
        => CreateWorkerFunction(stateCallback: null);

    /// <inheritdoc/>
    public Func<string, CancellationToken, Task> CreateWorkerFunction(Action<bool>? stateCallback)
    {
        return async (workerId, ct) =>
        {
            _logger.LogDebug("Dynamic worker {WorkerId} started", workerId);

            try
            {
                await ConsumeQueueAsync(workerId, stateCallback, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Expected during shutdown
            }

            _logger.LogDebug("Dynamic worker {WorkerId} stopped", workerId);
        };
    }

    /// <inheritdoc/>
    public Task RequestScaleUpAsync(int count, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc/>
    public Task RequestScaleDownAsync(int count, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc/>
    public async Task DrainAsync(CancellationToken ct = default)
    {
        // Stop accepting new work
        CompleteQueue();

        // Wait for workers to finish processing remaining items
        // (workers exit naturally once the completed queue is empty and the wait
        // reports false)
        await Task.WhenAll(_workers).WaitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public CancellationToken GetShutdownToken() => _cts.Token;

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken ct = default)
    {
        // Cancel the shutdown token first to signal all workers (including dynamic ones)
        await _cts.CancelAsync().ConfigureAwait(false);

        CompleteQueue();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(30)); // Graceful shutdown timeout

        try
        {
            await Task.WhenAll(_workers).WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Timeout or caller cancellation occurred.
            // Workers already signalled to cancel via _cts above.
            // Use a short additional grace period, but don't wait indefinitely.
            using var graceCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Task.WhenAll(_workers).WaitAsync(graceCts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        CompleteQueue();
        await _cts.CancelAsync().ConfigureAwait(false);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await Task.WhenAll(_workers)
                .WaitAsync(timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("DisposeAsync timed out waiting for workers to complete");
        }

        _cts.Dispose();
    }

    /// <summary>
    /// Attempts to read the next queued <see cref="WorkEnvelope{TWork}"/> directly
    /// from the internal queue. Test-only inspection hook (via InternalsVisibleTo)
    /// for asserting envelope metadata without widening the public surface.
    /// </summary>
    /// <param name="envelope">The dequeued envelope, when one was available.</param>
    /// <returns><see langword="true"/> if an envelope was read; otherwise <see langword="false"/>.</returns>
    internal bool TryReadEnvelope(out WorkEnvelope<TWork> envelope)
        => _queue.TryDequeue(out envelope);

    /// <summary>
    /// Resolves the effective <see cref="WorkClass"/> for an enqueue (DR-2).
    /// Precedence rule: a per-call class other than <see cref="WorkClass.Default"/>
    /// wins; a per-call <see cref="WorkClass.Default"/> defers to the classifier;
    /// with no classifier the class stays <see cref="WorkClass.Default"/>. The
    /// classifier delegate is invoked only on the per-call-Default path, so the
    /// hot path stays allocation-free. Sole classification site — every enqueue
    /// overload routes through here (Run/TryRun via TryEnqueue).
    /// </summary>
    /// <param name="work">The work item, passed to the classifier when consulted.</param>
    /// <param name="perCall">The per-call work class supplied by the caller.</param>
    /// <returns>The effective work class for the envelope.</returns>
    private WorkClass ResolveClass(TWork work, WorkClass perCall)
    {
        if (perCall != WorkClass.Default)
        {
            return perCall;
        }

        return _classifier is null ? WorkClass.Default : _classifier(work);
    }

    /// <summary>
    /// The FIFO producer-wait slow path, entered only when the fast-path try-write
    /// failed (queue full or completed): awaits the channel's own
    /// <see cref="FifoChannelWorkQueue{T}.WriteAsync(T, CancellationToken)"/> — the
    /// SINGLE async layer on the enqueue path (DR-7) — and maps the channel's native
    /// faults to <see cref="RejectionReason.Shutdown"/>: completion surfaces as
    /// <see cref="ChannelClosedException"/> and cancellation as
    /// <see cref="OperationCanceledException"/>; neither escapes to callers.
    /// </summary>
    /// <param name="fifo">The FIFO binding (devirtualized via the caller's pattern match).</param>
    /// <param name="envelope">The envelope to enqueue.</param>
    /// <param name="ct">Token whose cancellation abandons the wait.</param>
    /// <returns>The admission outcome.</returns>
    private static async ValueTask<EnqueueResult> EnqueueFifoSlowAsync(
        FifoChannelWorkQueue<WorkEnvelope<TWork>> fifo,
        WorkEnvelope<TWork> envelope,
        CancellationToken ct)
    {
        try
        {
            await fifo.WriteAsync(envelope, ct).ConfigureAwait(false);
            return EnqueueResult.Accepted;
        }
        catch (OperationCanceledException)
        {
            return EnqueueResult.Rejected(RejectionReason.Shutdown);
        }
        catch (ChannelClosedException)
        {
            return EnqueueResult.Rejected(RejectionReason.Shutdown);
        }
    }

    /// <summary>
    /// Marks the queue complete for adding. <c>Complete</c> is a concrete-only
    /// member by design (not part of <see cref="IWorkQueue{T}"/>), so this
    /// switches over the three sealed bindings — and records completion locally so
    /// the fail-fast enqueue path can report <see cref="RejectionReason.Shutdown"/>
    /// rather than <see cref="RejectionReason.CapacityExceeded"/> afterwards.
    /// </summary>
    private void CompleteQueue()
    {
        _queueCompleted = true;

        switch (_queue)
        {
            case FifoChannelWorkQueue<WorkEnvelope<TWork>> fifo:
                fifo.Complete();
                break;
            case ConcurrentPriorityWorkQueue<TWork> multiQueue:
                multiQueue.Complete();
                break;
            case LockingPriorityWorkQueue<TWork> locking:
                locking.Complete();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown work queue binding: {_queue.GetType()}.");
        }
    }

    /// <summary>
    /// Static worker loop: wraps the canonical queue-consume loop with start/stop
    /// logging and the shutdown exception boundary.
    /// </summary>
    /// <param name="workerId">The identifier used in worker log messages.</param>
    /// <param name="ct">Token whose cancellation signals orderly worker shutdown.</param>
    /// <returns>A task that completes when the worker exits.</returns>
    private async Task WorkerLoopAsync(string workerId, CancellationToken ct)
    {
        _logger.LogDebug("Worker {WorkerId} started", workerId);

        try
        {
            await ConsumeQueueAsync(workerId, stateCallback: null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected during shutdown
        }

        _logger.LogDebug("Worker {WorkerId} stopped", workerId);
    }

    /// <summary>
    /// The canonical <see cref="IWorkQueue{T}"/> consume loop shared by static and
    /// dynamic workers: await the wake-up, then drain via try-dequeue, tolerating
    /// spurious misses by looping back to the wait per the queue contract.
    /// </summary>
    /// <param name="workerId">The identifier used in worker log messages.</param>
    /// <param name="stateCallback">
    /// Optional busy/idle callback invoked around each handled item (dynamic workers).
    /// </param>
    /// <param name="ct">
    /// Token whose cancellation signals orderly shutdown: per the
    /// <see cref="IWorkQueue{T}"/> contract the wait MAY surface it as
    /// <see cref="OperationCanceledException"/> (bindings forward their primitive's
    /// native cancellation, keeping suspending waits on pooled sources); residual
    /// items are abandoned to preserve the pre-rewrite stop semantics. Queue
    /// COMPLETION (drain) still exits the loop via a <see langword="false"/> wait.
    /// </param>
    /// <returns>A task that completes when the queue is finished or shutdown is signalled.</returns>
    /// <exception cref="OperationCanceledException">
    /// Thrown when cancellation of <paramref name="ct"/> surfaces from the queue wait
    /// or is observed by the handler. Callers own this catch — once per worker,
    /// AROUND the loop (the canonical-loop boundary), never per wait.
    /// </exception>
    private async Task ConsumeQueueAsync(string workerId, Action<bool>? stateCallback, CancellationToken ct)
    {
        while (await _queue.WaitToDequeueAsync(ct).ConfigureAwait(false))
        {
            while (!ct.IsCancellationRequested && _queue.TryDequeue(out var envelope))
            {
                var observer = QueueWaitObserved;
                observer?.Invoke(envelope.Class, _timeProvider.GetElapsedTime(envelope.EnqueuedAtTicks));

                stateCallback?.Invoke(true); // Mark busy
                try
                {
                    await _handler.HandleAsync(envelope.Work, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Worker {WorkerId} failed to process work item: {WorkItem}", workerId, envelope.Work);
                }
                finally
                {
                    stateCallback?.Invoke(false); // Mark idle
                }
            }
        }
    }
}
