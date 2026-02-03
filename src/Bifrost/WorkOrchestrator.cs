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
/// Key design principles:
/// <list type="bullet">
///   <item><description>ValueTask-based API for zero-allocation hot path</description></item>
///   <item><description>Non-allocating property access for observability</description></item>
///   <item><description>Escape hatch via Writer property for advanced scenarios</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class WorkOrchestrator<TWork> : IWorkOrchestrator<TWork>
{
    private readonly Channel<TWork> _channel;
    private readonly IWorkHandler<TWork> _handler;
    private readonly ILogger<WorkOrchestrator<TWork>> _logger;
    private readonly Task[] _workers;
    private readonly CancellationTokenSource _cts = new();
    private readonly int _capacity;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkOrchestrator{TWork}"/> class.
    /// </summary>
    /// <param name="handler">The handler that processes work items.</param>
    /// <param name="options">Configuration options for the orchestrator.</param>
    /// <param name="logger">The logger instance.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    public WorkOrchestrator(
        IWorkHandler<TWork> handler,
        IOptions<WorkOrchestratorOptions> options,
        ILogger<WorkOrchestrator<TWork>> logger)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _handler = handler;
        _logger = logger;

        var opts = options.Value;
        _capacity = opts.Capacity;

        _channel = Channel.CreateBounded<TWork>(new BoundedChannelOptions(opts.Capacity)
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
    public ChannelWriter<TWork> Writer => _channel.Writer;

    /// <inheritdoc/>
    public ValueTask EnqueueAsync(TWork work, CancellationToken ct = default)
    {
        return _channel.Writer.WriteAsync(work, ct);
    }

    /// <inheritdoc/>
    public bool TryEnqueue(TWork work)
    {
        return _channel.Writer.TryWrite(work);
    }

    /// <inheritdoc/>
    public void Run(TWork work)
    {
        if (!_channel.Writer.TryWrite(work))
        {
            throw new InvalidOperationException("Queue is full");
        }
    }

    /// <inheritdoc/>
    public bool TryRun(TWork work)
    {
        return _channel.Writer.TryWrite(work);
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
                await foreach (var work in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    stateCallback?.Invoke(true); // Mark busy
                    try
                    {
                        await _handler.HandleAsync(work, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Dynamic worker {WorkerId} failed to process work item", workerId);
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
        await Task.WhenAll(_workers).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _cts.Dispose();
    }

    private async Task WorkerLoopAsync(string workerId, CancellationToken ct)
    {
        _logger.LogDebug("Worker {WorkerId} started", workerId);

        try
        {
            await foreach (var work in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await _handler.HandleAsync(work, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Worker {WorkerId} failed to process work item", workerId);
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
