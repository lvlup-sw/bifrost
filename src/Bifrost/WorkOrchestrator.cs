// =============================================================================
// <copyright file="WorkOrchestrator.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

using Bifrost.Core;
using Bifrost.Queues;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bifrost;

/// <summary>Owns an in-memory bounded queue and its registered workers.</summary>
/// <typeparam name="TWork">The work item type.</typeparam>
public sealed class WorkOrchestrator<TWork> : IWorkOrchestrator<TWork>
{
    private readonly IWorkQueue<WorkEnvelope<TWork>> _queue;
    private readonly IWorkQueueLifecycle _lifecycle;
    private readonly ILeasedWorkQueue<WorkEnvelope<TWork>>? _leased;
    private readonly IWorkHandler<TWork> _handler;
    private readonly ILogger<WorkOrchestrator<TWork>> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationToken _shutdownToken;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TWork, WorkClass>? _classifier;
    private readonly QueueFullMode _fullMode;
    private readonly Lock _lifecycleGate = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _queueCompleted;
    private int _activeWorkers;
    private Task? _disposeTask;
    private Task? _abortSignal;
    private Exception? _workerError;

    /// <summary>Creates the selected queue and starts the initial workers.</summary>
    /// <param name="handler">The work handler.</param>
    /// <param name="options">Ordering, capacity, and worker settings.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The timestamp source.</param>
    /// <param name="classifier">The class used when per-item options omit it.</param>
    public WorkOrchestrator(IWorkHandler<TWork> handler, IOptions<WorkOrchestratorOptions> options,
        ILogger<WorkOrchestrator<TWork>> logger, TimeProvider? timeProvider = null,
        Func<TWork, WorkClass>? classifier = null)
        : this(null, handler, options, logger, timeProvider, classifier)
    {
    }

    /// <summary>Uses a queue from <paramref name="queueFactory"/> and starts the initial workers.</summary>
    /// <param name="handler">The work handler.</param>
    /// <param name="options">Capacity and worker settings. The dispatch strategy is not used to pick the queue.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The timestamp source.</param>
    /// <param name="classifier">The class used when per-item options omit it.</param>
    /// <param name="queueFactory">
    /// Creates the queue from the options and the resolved timestamp source. The orchestrator owns
    /// the result and disposes it when it is disposable.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when the queue does not implement <see cref="IWorkQueueLifecycle"/>, when
    /// <see cref="QueueFullMode.Wait"/> is configured for a queue that is not a
    /// <see cref="FifoChannelWorkQueue{T}"/>, or when the queue is leased (for example a
    /// <see cref="KeyedWorkQueue{TWork, TKey}"/>) and the options do not select
    /// <see cref="QueueFullMode.Reject"/> and <see cref="DispatchStrategy.Fifo"/>.
    /// </exception>
    /// <remarks>
    /// This constructor has no optional parameters, so a call with the public constructor's
    /// arguments never binds to it.
    /// </remarks>
    internal WorkOrchestrator(IWorkHandler<TWork> handler, IOptions<WorkOrchestratorOptions> options,
        ILogger<WorkOrchestrator<TWork>> logger, TimeProvider? timeProvider, Func<TWork, WorkClass>? classifier,
        Func<WorkOrchestratorOptions, TimeProvider, IWorkQueue<WorkEnvelope<TWork>>> queueFactory)
        : this(queueFactory ?? throw new ArgumentNullException(nameof(queueFactory)), handler, options, logger,
            timeProvider, classifier)
    {
    }

    /// <summary>Shared initializer for the public and the factory constructors.</summary>
    /// <param name="queueFactory">The injected queue factory, or <see langword="null"/> to select a built-in binding from the dispatch strategy.</param>
    /// <param name="handler">The work handler.</param>
    /// <param name="options">Ordering, capacity, and worker settings.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The timestamp source.</param>
    /// <param name="classifier">The class used when per-item options omit it.</param>
    /// <remarks>
    /// The strategy guard runs before a built-in binding is built, so the public constructor keeps
    /// its exception order and message. The general guard runs on the resolved queue:
    /// <see cref="QueueFullMode.Wait"/> needs <see cref="FifoChannelWorkQueue{T}"/>, which makes
    /// the cast in <see cref="EnqueueAsync"/> safe by construction.
    /// </remarks>
    private WorkOrchestrator(Func<WorkOrchestratorOptions, TimeProvider, IWorkQueue<WorkEnvelope<TWork>>>? queueFactory,
        IWorkHandler<TWork> handler, IOptions<WorkOrchestratorOptions> options,
        ILogger<WorkOrchestrator<TWork>> logger, TimeProvider? timeProvider, Func<TWork, WorkClass>? classifier)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        var opts = options.Value;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(opts.Capacity);
        ArgumentOutOfRangeException.ThrowIfNegative(opts.WorkerCount);
        if (!Enum.IsDefined(opts.FullMode)) throw new ArgumentOutOfRangeException(nameof(options));
        if (queueFactory is null && opts.DispatchStrategy != DispatchStrategy.Fifo && opts.FullMode != QueueFullMode.Reject)
            throw new ArgumentException("Priority dispatch supports only Reject full mode.", nameof(options));
        _handler = handler;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _classifier = classifier;
        _fullMode = opts.FullMode;
        _shutdownToken = _cts.Token;
        Capacity = opts.Capacity;
        Ordering = opts.DispatchStrategy;
        _queue = queueFactory is null ? CreateBuiltInQueue(opts, _timeProvider) : queueFactory(opts, _timeProvider);
        _lifecycle = ValidateQueue(_queue, opts);
        _leased = _queue as ILeasedWorkQueue<WorkEnvelope<TWork>>;
        _activeWorkers = opts.WorkerCount;
        for (int i = 0; i < opts.WorkerCount; i++)
        {
            string id = $"Worker-{i}";
            _ = Task.Factory.StartNew(() => RunRegisteredWorkerAsync(id, null, _shutdownToken),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        }
    }

    private static IWorkQueue<WorkEnvelope<TWork>> CreateBuiltInQueue(WorkOrchestratorOptions opts, TimeProvider timeProvider)
        => opts.DispatchStrategy switch
        {
            DispatchStrategy.Fifo => new FifoChannelWorkQueue<WorkEnvelope<TWork>>(opts.Capacity),
            DispatchStrategy.PriorityExact => new LockingPriorityWorkQueue<TWork>(opts.Capacity, opts.Priority, timeProvider.TimestampFrequency),
            DispatchStrategy.PriorityRelaxed => new ConcurrentPriorityWorkQueue<TWork>(opts.Capacity, opts.Priority, timeProvider.TimestampFrequency),
            _ => throw new ArgumentOutOfRangeException("options", "Unknown dispatch strategy."),
        };

    /// <summary>Checks the resolved queue and returns its lifecycle hooks.</summary>
    /// <param name="queue">The resolved queue.</param>
    /// <param name="opts">The configured options.</param>
    /// <returns>The queue's lifecycle hooks.</returns>
    /// <remarks>
    /// <para>
    /// Every queue must implement <see cref="IWorkQueueLifecycle"/>. A leased queue (keyed
    /// dispatch) also needs <see cref="QueueFullMode.Reject"/>, because a saturated key must
    /// reject at once instead of parking the producer, and <see cref="DispatchStrategy.Fifo"/>,
    /// because keyed dispatch cannot be combined with class priority. These leased checks run
    /// before the general Wait check so that a keyed misconfiguration gets a keyed message.
    /// </para>
    /// <para>A rejected queue is disposed when it is disposable, because no orchestrator will own it.</para>
    /// </remarks>
    private static IWorkQueueLifecycle ValidateQueue(IWorkQueue<WorkEnvelope<TWork>>? queue, WorkOrchestratorOptions opts)
    {
        if (queue is not IWorkQueueLifecycle lifecycle)
            throw RejectQueue(queue, "The work queue must implement IWorkQueueLifecycle.", "queueFactory");

        if (queue is ILeasedWorkQueue<WorkEnvelope<TWork>>)
        {
            if (opts.FullMode != QueueFullMode.Reject)
                throw RejectQueue(queue, "Keyed dispatch supports only Reject full mode.", "options");
            if (opts.DispatchStrategy != DispatchStrategy.Fifo)
                throw RejectQueue(queue, "Keyed dispatch cannot be combined with class priority dispatch.", "options");
        }

        if (opts.FullMode == QueueFullMode.Wait && queue is not FifoChannelWorkQueue<WorkEnvelope<TWork>>)
            throw RejectQueue(queue, "Wait full mode requires the FIFO channel queue.", "options");

        return lifecycle;
    }

    private static ArgumentException RejectQueue(IWorkQueue<WorkEnvelope<TWork>>? queue, string message, string paramName)
    {
        (queue as IDisposable)?.Dispose();
        return new ArgumentException(message, paramName);
    }

    /// <summary>Gets the configured ordering policy.</summary>
    public DispatchStrategy Ordering { get; }

    /// <inheritdoc/>
    public int PendingCount => _queue.Count;

    /// <inheritdoc/>
    public int ActiveWorkers => Volatile.Read(ref _activeWorkers);

    /// <inheritdoc/>
    public int Capacity { get; }

    /// <inheritdoc/>
    public Task Completion => _completion.Task;
    internal IWorkQueue<WorkEnvelope<TWork>> WorkQueue => _queue;
    internal Action<WorkClass, TimeSpan>? QueueWaitObserved { get; set; }

    /// <inheritdoc/>
    public EnqueueResult Enqueue(TWork work, WorkEnqueueOptions options = default)
        => _queue.Enqueue(CreateEnvelope(work, options));

    /// <inheritdoc/>
    public bool TryEnqueue(TWork work, WorkEnqueueOptions options = default) => Enqueue(work, options).IsAccepted;

    /// <inheritdoc/>
    /// <remarks>
    /// The FIFO cast on the waiting path is safe by construction: the constructor admits
    /// <see cref="QueueFullMode.Wait"/> only when the queue is the FIFO channel queue.
    /// </remarks>
    public ValueTask<EnqueueResult> EnqueueAsync(TWork work, WorkEnqueueOptions options = default, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var envelope = CreateEnvelope(work, options);
        var result = _queue.Enqueue(envelope);
        if (result.IsAccepted || result.Reason == RejectionReason.Shutdown || _fullMode == QueueFullMode.Reject)
            return new(result);
        return EnqueueFifoSlowAsync((FifoChannelWorkQueue<WorkEnvelope<TWork>>)_queue, envelope, ct);
    }

    private WorkEnvelope<TWork> CreateEnvelope(TWork work, WorkEnqueueOptions options)
    {
        var workClass = options.Class ?? _classifier?.Invoke(work) ?? WorkClass.Default;
        if (!Enum.IsDefined(workClass)) throw new ArgumentOutOfRangeException(nameof(options));
        return new(work, workClass, _timeProvider.GetTimestamp());
    }

    private static async ValueTask<EnqueueResult> EnqueueFifoSlowAsync(
        FifoChannelWorkQueue<WorkEnvelope<TWork>> fifo, WorkEnvelope<TWork> envelope, CancellationToken ct)
    {
        try
        {
            await fifo.WriteAsync(envelope, ct);
            return EnqueueResult.Accepted;
        }
        catch (ChannelClosedException)
        {
            return EnqueueResult.Rejected(RejectionReason.Shutdown);
        }
    }

    /// <inheritdoc/>
    public void Complete()
    {
        lock (_lifecycleGate)
        {
            if (_queueCompleted) return;
            _lifecycle.Complete();
            _queueCompleted = true;
            if (_activeWorkers == 0) FinishCompletion();
        }
    }

    /// <inheritdoc/>
    public Task DrainAsync(CancellationToken ct = default)
    {
        Complete();
        return Completion.WaitAsync(ct);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The method waits for both the cancellation signal and <c>Completion</c>. A failure in a
    /// cancellation callback must not let disposal race workers that still use owned resources.
    /// </remarks>
    public async Task AbortAsync(CancellationToken ct = default)
    {
        Complete();
        Task signal;
        lock (_lifecycleGate) signal = _abortSignal ??= _cts.CancelAsync();
        await Task.WhenAll(signal, Completion).WaitAsync(ct);
    }

    /// <inheritdoc/>
    public CancellationToken GetShutdownToken() => _shutdownToken;

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        lock (_lifecycleGate) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private void FinishCompletion()
    {
        if (_workerError is { } error) _completion.TrySetException(error);
        else _completion.TrySetResult();
    }

    // No timeout may authorize disposal while a handler still uses the queue or the token, so the
    // owned resources are disposed only after AbortAsync completes.
    private async Task DisposeCoreAsync()
    {
        try
        {
            await AbortAsync();
        }
        finally
        {
            if (_queue is IDisposable disposable) disposable.Dispose();
            _cts.Dispose();
        }
    }

    /// <inheritdoc/>
    public Func<string, CancellationToken, Task> CreateWorkerFunction() => CreateWorkerFunction(null);

    /// <inheritdoc/>
    public Func<string, CancellationToken, Task> CreateWorkerFunction(Action<bool>? stateCallback)
        => CreateWorkerFunction(stateCallback, CancellationToken.None);

    /// <inheritdoc/>
    public Func<string, CancellationToken, Task> CreateWorkerFunction(Action<bool>? stateCallback, CancellationToken retirementToken)
        => (id, token) =>
        {
            lock (_lifecycleGate)
            {
                if (_queueCompleted) return Task.CompletedTask;
                _activeWorkers++;
            }

            return RunRegisteredWorkerAsync(id, stateCallback, token, retirementToken);
        };

    // The method allocates the extra linked source for retirement (dequeueCancellation) once per
    // dynamic worker, never per item. A priority wait can acquire a permit just as retirement wins.
    // So the finally block of a retired worker hands a wake to a remaining consumer, and an accepted
    // item cannot be left without a signal.
    private async Task RunRegisteredWorkerAsync(string id, Action<bool>? callback, CancellationToken token,
        CancellationToken retirementToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdownToken);
        using var dequeueCancellation = retirementToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(linked.Token, retirementToken)
            : null;
        var dequeueToken = dequeueCancellation?.Token ?? linked.Token;
        try
        {
            await ConsumeQueueAsync(id, callback, linked.Token, dequeueToken);
            await DrainRetainedWorkAsync(id, callback, linked.Token, retirementToken);
        }
        catch (OperationCanceledException) when (dequeueToken.IsCancellationRequested)
        {
            await DrainRetainedWorkAsync(id, callback, linked.Token, retirementToken);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Worker {WorkerId} failed", id);
            lock (_lifecycleGate) _workerError ??= error;
        }
        finally
        {
            if (retirementToken.IsCancellationRequested) _lifecycle.WakeConsumer();

            lock (_lifecycleGate)
            {
                _activeWorkers--;
                if (_queueCompleted && _activeWorkers == 0) FinishCompletion();
            }
        }
    }

    /// <summary>
    /// Dequeues items already admitted when retirement has cancelled this worker and it is the
    /// only consumer left. A stop request must not let <see cref="DrainAsync"/> succeed while
    /// those items are still queued. Future arrivals wait for another consumer.
    /// </summary>
    /// <remarks>
    /// The method does not wait for a permit. A missing permit belongs to the wake hand-off in the
    /// caller, and a blocking wait here stalls that hand-off on the same worker.
    /// </remarks>
    private async Task DrainRetainedWorkAsync(string id, Action<bool>? callback, CancellationToken handlerToken,
        CancellationToken retirementToken)
    {
        while (true)
        {
            lock (_lifecycleGate)
            {
                if (!retirementToken.IsCancellationRequested || _activeWorkers != 1 || _queue.Count == 0)
                    return;
            }

            if (!TryDequeueEnvelope(out var envelope, out var lease))
                return;

            await ProcessEnvelopeAsync(id, callback, handlerToken, envelope, lease);
        }
    }

    /// <summary>Runs the handler pipeline for one dequeued item and then returns its lease.</summary>
    /// <param name="workerId">The worker's identifier, used in logs.</param>
    /// <param name="stateCallback">The worker's busy/idle observer, if any.</param>
    /// <param name="ct">The handler token.</param>
    /// <param name="envelope">The dequeued item.</param>
    /// <param name="lease">The lease from a leased binding, or <see langword="null"/> when there is nothing to release.</param>
    /// <remarks>
    /// <para>
    /// The order is fixed. First the handler pipeline exits: the decorators, every dead-letter
    /// retry, and the disposal of a scoped handler's DI scope all happen inside
    /// <see cref="IWorkHandler{TWork}.HandleAsync"/>. Then the lease is released through
    /// <see cref="ILeasedWorkQueue{T}.Release"/>. Last, the worker reports itself idle. This holds
    /// on success, on a handler failure, and when cancellation rethrows.
    /// </para>
    /// <para>
    /// The release runs in a <c>try</c> whose <c>finally</c> reports idle, so a throwing release
    /// cannot leave the busy flag set. Its exception then propagates to the caller. From the
    /// consume loop, and from the drain after it, that is the worker's error path, which logs the
    /// exception and faults completion. From the retirement drain inside the cancellation
    /// <c>catch</c> of <see cref="RunRegisteredWorkerAsync"/>, the exception skips that error path
    /// and faults only the worker's task.
    /// </para>
    /// </remarks>
    private async Task ProcessEnvelopeAsync(string workerId, Action<bool>? stateCallback, CancellationToken ct,
        WorkEnvelope<TWork> envelope, object? lease)
    {
        var observer = QueueWaitObserved;
        if (observer is not null) NotifyQueueWait(observer, in envelope, workerId);
        if (stateCallback is not null) NotifyWorkerState(stateCallback, true, workerId);
        try
        {
            await _handler.HandleAsync(envelope.Work, ct);
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
            try
            {
                if (lease is not null) _leased!.Release(lease);
            }
            finally
            {
                if (stateCallback is not null) NotifyWorkerState(stateCallback, false, workerId);
            }
        }
    }

    /// <summary>Dequeues the next item through the leased path when the binding has one.</summary>
    /// <param name="envelope">The dequeued item when the method returns <c>true</c>.</param>
    /// <param name="lease">The item's lease, or <see langword="null"/> for a binding without leases.</param>
    /// <returns><c>true</c> if an item was dequeued; <c>false</c> on a miss.</returns>
    private bool TryDequeueEnvelope([MaybeNullWhen(false)] out WorkEnvelope<TWork> envelope, out object? lease)
    {
        if (_leased is not null) return _leased.TryDequeue(out envelope, out lease);
        lease = null;
        return _queue.TryDequeue(out envelope);
    }

    private void NotifyQueueWait(Action<WorkClass, TimeSpan> observer, in WorkEnvelope<TWork> envelope, string workerId)
    {
        try
        {
            observer(envelope.Class, _timeProvider.GetElapsedTime(envelope.EnqueuedAtTicks));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Queue-wait observer failed for worker {WorkerId}", workerId);
        }
    }

    private void NotifyWorkerState(Action<bool> callback, bool busy, string workerId)
    {
        try
        {
            callback(busy);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "State observer failed for worker {WorkerId}", workerId);
        }
    }

    /// <inheritdoc/>
    public Task RequestScaleUpAsync(int count, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task RequestScaleDownAsync(int count, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>Dequeues one item directly, without a worker. Test inspection only.</summary>
    /// <param name="envelope">The dequeued item when the method returns <c>true</c>.</param>
    /// <returns><c>true</c> if an item was dequeued; <c>false</c> on a miss.</returns>
    /// <remarks>
    /// This reads through the plain <see cref="IWorkQueue{T}.TryDequeue(out T)"/>, so it is valid
    /// only for bindings that return no lease. Every strategy-selected binding qualifies;
    /// <see cref="KeyedWorkQueue{TWork, TKey}"/> does not.
    /// </remarks>
    internal bool TryReadEnvelope(out WorkEnvelope<TWork> envelope) => _queue.TryDequeue(out envelope);

    // The wait consumes one permit. Each further item in the inner loop was funded by its own
    // enqueue, so ConsumeMatchedWake takes that permit too. Otherwise a drain leaves a surplus of
    // permits, and later wakes spin on an empty queue.
    private async Task ConsumeQueueAsync(string workerId, Action<bool>? stateCallback, CancellationToken ct, CancellationToken dequeueToken)
    {
        while (!dequeueToken.IsCancellationRequested && await _queue.WaitToDequeueAsync(dequeueToken))
        {
            if (!TryDequeueEnvelope(out var envelope, out var lease))
                continue;

            await ProcessEnvelopeAsync(workerId, stateCallback, ct, envelope, lease);
            while (!dequeueToken.IsCancellationRequested && TryDequeueEnvelope(out envelope, out lease))
            {
                _lifecycle.ConsumeMatchedWake();
                await ProcessEnvelopeAsync(workerId, stateCallback, ct, envelope, lease);
            }
        }
    }
}
