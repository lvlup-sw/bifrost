// =============================================================================
// <copyright file="WorkOrchestrator.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

using Bifrost.Core;
using Bifrost.Outcomes;
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
    private readonly TerminalOutcomeStage<TWork> _outcomes;
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
        : this(null, handler, options, logger, timeProvider, classifier, null)
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
    /// <see cref="QueueFullMode.Reject"/> and either <see cref="DispatchStrategy.Fifo"/> or
    /// <see cref="DispatchStrategy.Keyed"/>, or when the options select
    /// <see cref="DispatchStrategy.Keyed"/> and the queue is not leased.
    /// </exception>
    /// <remarks>
    /// This constructor has no optional parameters, so a call with the public constructor's
    /// arguments never binds to it.
    /// </remarks>
    internal WorkOrchestrator(IWorkHandler<TWork> handler, IOptions<WorkOrchestratorOptions> options,
        ILogger<WorkOrchestrator<TWork>> logger, TimeProvider? timeProvider, Func<TWork, WorkClass>? classifier,
        Func<WorkOrchestratorOptions, TimeProvider, IWorkQueue<WorkEnvelope<TWork>>> queueFactory)
        : this(queueFactory ?? throw new ArgumentNullException(nameof(queueFactory)), handler, options, logger,
            timeProvider, classifier, null)
    {
    }

    /// <summary>Creates the orchestrator with the fixed services of its terminal outcome stage.</summary>
    /// <param name="handler">The work handler, with its handler decorators already applied.</param>
    /// <param name="options">Ordering, capacity, and worker settings.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The timestamp source.</param>
    /// <param name="classifier">The class used when per-item options omit it.</param>
    /// <param name="queueFactory">
    /// Creates the queue, or <see langword="null"/> to select a built-in binding from the dispatch strategy.
    /// </param>
    /// <param name="outcomes">The dead-letter, event-stream, and counter services of the terminal outcome stage.</param>
    /// <remarks>
    /// The builder uses this constructor, so dead-letter retries, capture, and completion events
    /// are wired from flags and run in fixed slots outside every handler decorator.
    /// </remarks>
    internal WorkOrchestrator(IWorkHandler<TWork> handler, IOptions<WorkOrchestratorOptions> options,
        ILogger<WorkOrchestrator<TWork>> logger, TimeProvider? timeProvider, Func<TWork, WorkClass>? classifier,
        Func<WorkOrchestratorOptions, TimeProvider, IWorkQueue<WorkEnvelope<TWork>>>? queueFactory,
        TerminalOutcomeOptions<TWork> outcomes)
        : this(queueFactory, handler, options, logger, timeProvider, classifier,
            outcomes ?? throw new ArgumentNullException(nameof(outcomes)))
    {
    }

    /// <summary>Shared initializer for the public and the factory constructors.</summary>
    /// <param name="queueFactory">The injected queue factory, or <see langword="null"/> to select a built-in binding from the dispatch strategy.</param>
    /// <param name="handler">The work handler.</param>
    /// <param name="options">Ordering, capacity, and worker settings.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The timestamp source.</param>
    /// <param name="classifier">The class used when per-item options omit it.</param>
    /// <param name="outcomes">The terminal outcome services, or <see langword="null"/> for one attempt and no capture.</param>
    /// <remarks>
    /// The strategy guards run before a built-in binding is built, so the public constructor keeps
    /// its exception order and message. <see cref="DispatchStrategy.Keyed"/> has no built-in
    /// binding: it needs the key selector and limits that only the factory constructor receives.
    /// The general guard runs on the resolved queue: <see cref="QueueFullMode.Wait"/> needs
    /// <see cref="FifoChannelWorkQueue{T}"/>, which makes the cast in <see cref="EnqueueAsync"/>
    /// safe by construction.
    /// </remarks>
    private WorkOrchestrator(Func<WorkOrchestratorOptions, TimeProvider, IWorkQueue<WorkEnvelope<TWork>>>? queueFactory,
        IWorkHandler<TWork> handler, IOptions<WorkOrchestratorOptions> options,
        ILogger<WorkOrchestrator<TWork>> logger, TimeProvider? timeProvider, Func<TWork, WorkClass>? classifier,
        TerminalOutcomeOptions<TWork>? outcomes)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        var opts = options.Value;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(opts.Capacity);
        ArgumentOutOfRangeException.ThrowIfNegative(opts.WorkerCount);
        if (!Enum.IsDefined(opts.FullMode)) throw new ArgumentOutOfRangeException(nameof(options));
        if (queueFactory is null && opts.DispatchStrategy == DispatchStrategy.Keyed)
            throw new ArgumentException("Keyed dispatch needs a key selector. Configure it with WithKeyedDispatch.", nameof(options));
        if (queueFactory is null && opts.DispatchStrategy != DispatchStrategy.Fifo && opts.FullMode != QueueFullMode.Reject)
            throw new ArgumentException("Priority dispatch supports only Reject full mode.", nameof(options));
        _handler = handler;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _outcomes = new TerminalOutcomeStage<TWork>(outcomes, logger, _timeProvider);
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
    /// reject at once instead of parking the producer, and <see cref="DispatchStrategy.Keyed"/>
    /// or <see cref="DispatchStrategy.Fifo"/>, because keyed dispatch cannot be combined with
    /// class priority. In the other direction, <see cref="DispatchStrategy.Keyed"/> needs a leased
    /// queue. These keyed checks run before the general Wait check so that a keyed
    /// misconfiguration gets a keyed message.
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
            if (opts.DispatchStrategy is not (DispatchStrategy.Fifo or DispatchStrategy.Keyed))
                throw RejectQueue(queue, "Keyed dispatch cannot be combined with class priority dispatch.", "options");
        }
        else if (opts.DispatchStrategy == DispatchStrategy.Keyed)
        {
            throw RejectQueue(queue, "Keyed dispatch needs a leased keyed work queue.", "options");
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

    /// <summary>Gets the number of queued items that a free worker could take now.</summary>
    /// <remarks>
    /// For a binding that can hold blocked items (keyed dispatch), this is the dispatchable part of
    /// the backlog. For every other binding each queued item is dispatchable, so it equals
    /// <see cref="PendingCount"/>.
    /// </remarks>
    internal int DispatchableCount => _queue is IDispatchableBacklog backlog ? backlog.DispatchableCount : _queue.Count;

    /// <summary>Gets the pending, ready, and blocked counts of the queue, read together.</summary>
    /// <remarks>
    /// <para>
    /// This is the aggregate surface for the autoscaler, health checks, and gauges.
    /// <see cref="DispatchBacklog.Pending"/> equals <see cref="PendingCount"/>, the total pending
    /// depth that capacity and drain use. <see cref="DispatchBacklog.Ready"/> equals
    /// <see cref="DispatchableCount"/>, the backlog that more workers could run.
    /// <see cref="DispatchBacklog.Blocked"/> counts the items that wait for a key slot.
    /// </para>
    /// <para>
    /// A keyed binding reads both counts under one lock. Every other binding has no blocked items,
    /// so it reports every pending item as ready.
    /// </para>
    /// </remarks>
    internal DispatchBacklog Backlog
    {
        get
        {
            if (_queue is IDispatchableBacklog backlog) return backlog.Backlog;
            var pending = _queue.Count;
            return new DispatchBacklog(pending, pending);
        }
    }

    internal Action<WorkClass, TimeSpan>? QueueWaitObserved { get; set; }

    /// <summary>Gets the terminal outcome stage: its counters, its attempt limit, and its observer hook.</summary>
    internal TerminalOutcomeStage<TWork> Outcomes => _outcomes;

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
    /// <para>
    /// The method closes admission, signals cancellation, and then runs the abandonment sweep
    /// before it waits: every item still queued, including items blocked behind a running item of
    /// the same key, is removed and settled as abandoned. Running handlers keep their slots until
    /// they exit. The sweep and the last worker's sweep run under the lifecycle gate, so every
    /// abandoned item is settled before <c>Completion</c> finishes.
    /// </para>
    /// <para>
    /// The method waits for both the cancellation signal and <c>Completion</c>. A failure in a
    /// cancellation callback must not let disposal race workers that still use owned resources.
    /// </para>
    /// </remarks>
    public async Task AbortAsync(CancellationToken ct = default)
    {
        Complete();
        Task signal;
        lock (_lifecycleGate)
        {
            signal = _abortSignal ??= _cts.CancelAsync();
            if (!_completion.Task.IsCompleted) SweepAbandoned();
        }

        await Task.WhenAll(signal, Completion).WaitAsync(ct);
    }

    /// <inheritdoc/>
    public CancellationToken GetShutdownToken() => _shutdownToken;

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        lock (_lifecycleGate) return new(_disposeTask ??= DisposeCoreAsync());
    }

    /// <summary>Completes <see cref="Completion"/> once the queue is complete and no worker is left.</summary>
    /// <remarks>
    /// No worker can start after this point, so the abandonment sweep removes every item still
    /// queued and settles it as abandoned before <see cref="Completion"/> finishes. After a drain
    /// the queue is empty, so the sweep finds nothing.
    /// </remarks>
    private void FinishCompletion()
    {
        if (!_completion.Task.IsCompleted) SweepAbandoned();
        if (_workerError is { } error) _completion.TrySetException(error);
        else _completion.TrySetResult();
    }

    /// <summary>Removes every queued item and settles each one as abandoned.</summary>
    /// <remarks>
    /// <para>
    /// This is the one sweep for every binding. It runs under the lifecycle gate, after the queue
    /// is completed, so no item can be admitted behind it. A leased binding removes its queued
    /// items in one step through <see cref="ILeasedWorkQueue{T}.DiscardQueued"/>, which reaches
    /// items blocked behind a running item and hands out no lease. Every other binding is emptied
    /// through its plain dequeue, which holds no slot.
    /// </para>
    /// <para>
    /// The sweep never takes or releases a lease. A running item's slot is released only by
    /// <see cref="ReleaseLeaseOnce"/> in that item's worker, so a sweep that runs while a handler
    /// is still in its <c>finally</c> block cannot release the slot twice.
    /// </para>
    /// </remarks>
    private void SweepAbandoned()
    {
        if (_leased is not null)
        {
            if (_queue.Count == 0) return;
            var discarded = new List<WorkEnvelope<TWork>>();
            _leased.DiscardQueued(discarded);
            for (var i = 0; i < discarded.Count; i++) _outcomes.SettleAbandoned();
            return;
        }

        while (_queue.TryDequeue(out _)) _outcomes.SettleAbandoned();
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

    /// <summary>Runs the handler attempts for one dequeued item, returns its lease, and settles its outcome.</summary>
    /// <param name="workerId">The worker's identifier, used in logs.</param>
    /// <param name="stateCallback">The worker's busy/idle observer, if any.</param>
    /// <param name="ct">The handler token.</param>
    /// <param name="envelope">The dequeued item.</param>
    /// <param name="lease">The lease from a leased binding, or <see langword="null"/> when there is nothing to release.</param>
    /// <remarks>
    /// <para>
    /// The order is fixed. First the attempt loop runs. Each attempt calls the decorated handler, so
    /// every handler decorator and every scoped handler's DI scope sees each attempt. A failed attempt
    /// is retried while <see cref="TerminalOutcomeStage{TWork}.MaxAttempts"/> allows it, except when
    /// it is an <see cref="OperationCanceledException"/> after the handler token was cancelled. Then
    /// the lease is released through <see cref="ILeasedWorkQueue{T}.Release"/>. Then the terminal
    /// outcome stage settles the item: it records one outcome, captures a final failure once, and
    /// publishes events. Last, the worker reports itself idle. This holds on success, on a handler
    /// failure, and when cancellation rethrows.
    /// </para>
    /// <para>
    /// The release runs in a <c>try</c> whose <c>finally</c> settles the outcome and then reports
    /// idle, so a throwing release skips neither. Its exception then propagates to the caller. From
    /// the consume loop, and from the drain after it, that is the worker's error path, which logs the
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
        var startedAt = _timeProvider.GetTimestamp();
        var outcome = TerminalOutcome.Failed;
        var attempts = 0;
        Exception? error = null;
        try
        {
            while (true)
            {
                attempts++;
                try
                {
                    await _handler.HandleAsync(envelope.Work, ct);
                    break;
                }
                catch (Exception ex) when (attempts < _outcomes.MaxAttempts && !IsHandlerCancellation(ex, ct))
                {
                    _outcomes.ReportRetry(ex, attempts);
                }
            }

            outcome = TerminalOutcome.Succeeded;
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            outcome = TerminalOutcome.Canceled;
            error = ex;
            throw;
        }
        catch (Exception ex)
        {
            error = ex;
            _logger.LogError(ex, "Worker {WorkerId} failed to process work item: {WorkItem}", workerId, envelope.Work);
        }
        finally
        {
            var duration = _timeProvider.GetElapsedTime(startedAt);
            try
            {
                ReleaseLeaseOnce(ref lease);
            }
            finally
            {
                try
                {
                    await _outcomes.SettleAsync(envelope.Work, outcome, attempts, error, duration);
                }
                finally
                {
                    if (stateCallback is not null) NotifyWorkerState(stateCallback, false, workerId);
                }
            }
        }
    }

    /// <summary>Returns the item's lease to the binding at most once.</summary>
    /// <param name="lease">The item's lease. It is set to <see langword="null"/> before the release runs.</param>
    /// <remarks>
    /// The lease is taken out of the variable before <see cref="ILeasedWorkQueue{T}.Release"/>
    /// runs, so a release that throws, or any later path through the same variable, cannot return
    /// it again. Together with the sweep, which holds no lease, this makes the worker that
    /// dequeued an item the only releaser of that item's slot.
    /// </remarks>
    private void ReleaseLeaseOnce(ref object? lease)
    {
        var held = Interlocked.Exchange(ref lease, null);
        if (held is not null) _leased!.Release(held);
    }

    private static bool IsHandlerCancellation(Exception error, CancellationToken ct)
        => error is OperationCanceledException && ct.IsCancellationRequested;

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
