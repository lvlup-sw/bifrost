// =============================================================================
// <copyright file="WorkOrchestrator.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

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
/// <see cref="IWorkQueue{T}"/> contract. The default binding is a strict-FIFO
/// bounded-channel queue; strategy selection is a construction concern layered on
/// later without changing this type's surface.
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
    private readonly FifoChannelWorkQueue<WorkEnvelope<TWork>> _queue;
    private readonly IWorkHandler<TWork> _handler;
    private readonly ILogger<WorkOrchestrator<TWork>> _logger;
    private readonly Task[] _workers;
    private readonly CancellationTokenSource _cts = new();
    private readonly int _capacity;
    private readonly TimeProvider _timeProvider;

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
    /// <exception cref="ArgumentNullException">Thrown when handler, options, or logger is null.</exception>
    public WorkOrchestrator(
        IWorkHandler<TWork> handler,
        IOptions<WorkOrchestratorOptions> options,
        ILogger<WorkOrchestrator<TWork>> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _handler = handler;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        var opts = options.Value;
        _capacity = opts.Capacity;

        _queue = new FifoChannelWorkQueue<WorkEnvelope<TWork>>(opts.Capacity);

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
    /// contract. Test-only inspection seam (via InternalsVisibleTo) that also marks
    /// the boundary where a binding will later be injected instead of constructed.
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
    public async ValueTask<EnqueueResult> EnqueueAsync(TWork work, WorkClass workClass = WorkClass.Default, CancellationToken ct = default)
    {
        var envelope = new WorkEnvelope<TWork>(work, workClass, _timeProvider.GetTimestamp());
        var accepted = await _queue.EnqueueAsync(envelope, ct).ConfigureAwait(false);
        return accepted ? EnqueueResult.Accepted : EnqueueResult.Rejected(RejectionReason.Shutdown);
    }

    /// <inheritdoc/>
    public bool TryEnqueue(TWork work, WorkClass workClass = WorkClass.Default)
    {
        return _queue.TryEnqueue(new WorkEnvelope<TWork>(work, workClass, _timeProvider.GetTimestamp()));
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
        _queue.Complete();

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

        _queue.Complete();

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
        _queue.Complete();
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
    /// Token whose cancellation signals orderly shutdown: the wait completes
    /// <see langword="false"/> and residual items are abandoned to preserve the
    /// pre-rewrite stop semantics.
    /// </param>
    /// <returns>A task that completes when the queue is finished or shutdown is signalled.</returns>
    /// <exception cref="OperationCanceledException">
    /// Rethrown when the handler observes cancellation of <paramref name="ct"/>;
    /// callers treat this as the shutdown boundary.
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
