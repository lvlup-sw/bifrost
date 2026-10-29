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

/// <summary>Owns an in-memory bounded queue and its registered workers.</summary>
/// <typeparam name="TWork">The work item type.</typeparam>
public sealed class WorkOrchestrator<TWork> : IWorkOrchestrator<TWork>
{
    private readonly IWorkQueue<WorkEnvelope<TWork>> _queue;
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
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        var opts = options.Value;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(opts.Capacity);
        ArgumentOutOfRangeException.ThrowIfNegative(opts.WorkerCount);
        if (!Enum.IsDefined(opts.FullMode)) throw new ArgumentOutOfRangeException(nameof(options));
        if (opts.DispatchStrategy != DispatchStrategy.Fifo && opts.FullMode != QueueFullMode.Reject)
            throw new ArgumentException("Priority dispatch supports only Reject full mode.", nameof(options));
        _handler = handler;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _classifier = classifier;
        _fullMode = opts.FullMode;
        _shutdownToken = _cts.Token;
        Capacity = opts.Capacity;
        Ordering = opts.DispatchStrategy;
        _queue = opts.DispatchStrategy switch
        {
            DispatchStrategy.Fifo => new FifoChannelWorkQueue<WorkEnvelope<TWork>>(opts.Capacity),
            DispatchStrategy.PriorityExact => new LockingPriorityWorkQueue<TWork>(opts.Capacity, opts.Priority, _timeProvider.TimestampFrequency),
            DispatchStrategy.PriorityRelaxed => new ConcurrentPriorityWorkQueue<TWork>(opts.Capacity, opts.Priority, _timeProvider.TimestampFrequency),
            _ => throw new ArgumentOutOfRangeException(nameof(options), "Unknown dispatch strategy."),
        };
        _activeWorkers = opts.WorkerCount;
        for (int i = 0; i < opts.WorkerCount; i++)
        {
            string id = $"Worker-{i}";
            _ = Task.Factory.StartNew(() => RunRegisteredWorkerAsync(id, null, _shutdownToken),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        }
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
            await fifo.WriteAsync(envelope, ct).ConfigureAwait(false);
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
            switch (_queue)
            {
                case FifoChannelWorkQueue<WorkEnvelope<TWork>> fifo: fifo.Complete(); break;
                case LockingPriorityWorkQueue<TWork> exact: exact.Complete(); break;
                case ConcurrentPriorityWorkQueue<TWork> relaxed: relaxed.Complete(); break;
            }

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
    public async Task AbortAsync(CancellationToken ct = default)
    {
        Complete();
        Task signal;
        lock (_lifecycleGate) signal = _abortSignal ??= _cts.CancelAsync();
        // Callback failures must not let disposal race workers still using owned resources.
        await Task.WhenAll(signal, Completion).WaitAsync(ct).ConfigureAwait(false);
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

    private async Task DisposeCoreAsync()
    {
        // No timeout may authorize disposal while a handler still uses the queue or token.
        try
        {
            await AbortAsync().ConfigureAwait(false);
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
        => (id, token) =>
        {
            lock (_lifecycleGate)
            {
                if (_queueCompleted) return Task.CompletedTask;
                _activeWorkers++;
            }

            return RunRegisteredWorkerAsync(id, stateCallback, token);
        };

    private async Task RunRegisteredWorkerAsync(string id, Action<bool>? callback, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdownToken);
        try
        {
            await ConsumeQueueAsync(id, callback, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Worker {WorkerId} failed", id);
            lock (_lifecycleGate) _workerError ??= error;
        }
        finally
        {
            lock (_lifecycleGate)
            {
                _activeWorkers--;
                if (_queueCompleted && _activeWorkers == 0) FinishCompletion();
            }
        }
    }

    /// <inheritdoc/>
    public Task RequestScaleUpAsync(int count, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task RequestScaleDownAsync(int count, CancellationToken cancellationToken = default) => Task.CompletedTask;
    internal bool TryReadEnvelope(out WorkEnvelope<TWork> envelope) => _queue.TryDequeue(out envelope);

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
