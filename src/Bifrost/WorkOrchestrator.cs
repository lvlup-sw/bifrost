// =============================================================================
// <copyright file="WorkOrchestrator.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Threading.Channels;

using Bifrost.Core;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bifrost;

/// <summary>
/// Orchestrates work processing through a bounded channel with configurable workers.
/// </summary>
/// <typeparam name="TWork">The type of work item to process.</typeparam>
/// <remarks>
/// <para>
/// This implementation provides a high-performance, zero-allocation hot path for
/// enqueueing work items. It manages a pool of workers that process items from the
/// internal channel.
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
    private readonly Channel<WorkEnvelope<TWork>> _channel;
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

        _channel = Channel.CreateBounded<WorkEnvelope<TWork>>(new BoundedChannelOptions(opts.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false, // Prevent stack dives
        });

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
    public int PendingCount => _channel.Reader.CanCount ? _channel.Reader.Count : 0;

    /// <inheritdoc/>
    public int ActiveWorkers => _workers.Length;

    /// <inheritdoc/>
    public int Capacity => _capacity;

    /// <inheritdoc/>
    public async ValueTask<EnqueueResult> EnqueueAsync(TWork work, WorkClass workClass = WorkClass.Default, CancellationToken ct = default)
    {
        try
        {
            await _channel.Writer
                .WriteAsync(new WorkEnvelope<TWork>(work, workClass, _timeProvider.GetTimestamp()), ct)
                .ConfigureAwait(false);
            return EnqueueResult.Accepted;
        }
        catch (ChannelClosedException)
        {
            return EnqueueResult.Rejected(RejectionReason.Shutdown);
        }
        catch (OperationCanceledException)
        {
            return EnqueueResult.Rejected(RejectionReason.Shutdown);
        }
    }

    /// <inheritdoc/>
    public bool TryEnqueue(TWork work, WorkClass workClass = WorkClass.Default)
    {
        return _channel.Writer.TryWrite(new WorkEnvelope<TWork>(work, workClass, _timeProvider.GetTimestamp()));
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
                await foreach (var envelope in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
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
                        _logger.LogError(ex, "Dynamic worker {WorkerId} failed to process work item: {WorkItem}", workerId, envelope.Work);
                    }
                    finally
                    {
                        stateCallback?.Invoke(false); // Mark idle
                    }
                }
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
        _channel.Writer.TryComplete();

        // Wait for workers to finish processing remaining items
        // (workers exit naturally when the channel reader completes)
        await Task.WhenAll(_workers).WaitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public CancellationToken GetShutdownToken() => _cts.Token;

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken ct = default)
    {
        // Cancel the shutdown token first to signal all workers (including dynamic ones)
        await _cts.CancelAsync().ConfigureAwait(false);

        _channel.Writer.TryComplete();

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
        _channel.Writer.TryComplete();
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
    /// from the internal channel. Test-only inspection hook (via InternalsVisibleTo)
    /// for asserting envelope metadata without widening the public surface.
    /// </summary>
    /// <param name="envelope">The dequeued envelope, when one was available.</param>
    /// <returns><see langword="true"/> if an envelope was read; otherwise <see langword="false"/>.</returns>
    internal bool TryReadEnvelope(out WorkEnvelope<TWork> envelope)
        => _channel.Reader.TryRead(out envelope);

    private async Task WorkerLoopAsync(string workerId, CancellationToken ct)
    {
        _logger.LogDebug("Worker {WorkerId} started", workerId);

        try
        {
            await foreach (var envelope in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
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
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected during shutdown
        }

        _logger.LogDebug("Worker {WorkerId} stopped", workerId);
    }
}
