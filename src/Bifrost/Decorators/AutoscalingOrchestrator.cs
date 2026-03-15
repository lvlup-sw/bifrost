// =============================================================================
// <copyright file="AutoscalingOrchestrator.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Threading.Channels;

using Bifrost.Autoscaling;
using Bifrost.Core;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bifrost.Decorators;

/// <summary>
/// Decorator that adds autoscaling capabilities to a work orchestrator.
/// </summary>
/// <typeparam name="TWork">The type of work item to process.</typeparam>
/// <remarks>
/// <para>
/// This decorator wraps an <see cref="IWorkOrchestrator{TWork}"/> and provides:
/// <list type="bullet">
///   <item><description>Metrics tracking for enqueue operations via <see cref="IWorkerMetrics"/></description></item>
///   <item><description>Dynamic worker scaling via <see cref="IWorkerRegistry"/></description></item>
///   <item><description>State-aware workers that report busy/idle status</description></item>
/// </list>
/// </para>
/// <para>
/// When <see cref="AutoscalingOptions.Enabled"/> is false, the decorator passes through
/// all operations without tracking metrics or managing dynamic workers.
/// </para>
/// </remarks>
public sealed class AutoscalingOrchestrator<TWork> : IWorkOrchestrator<TWork>
{
    private readonly IWorkOrchestrator<TWork> _inner;
    private readonly IWorkerRegistry _registry;
    private readonly IWorkerMetrics _metrics;
    private readonly AutoscalingOptions _options;
    private readonly ILogger<AutoscalingOrchestrator<TWork>> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoscalingOrchestrator{TWork}"/> class.
    /// </summary>
    /// <param name="inner">The inner orchestrator to wrap.</param>
    /// <param name="registry">The worker registry for dynamic scaling.</param>
    /// <param name="metrics">The metrics tracker for autoscaling.</param>
    /// <param name="options">The autoscaling configuration options wrapped in <see cref="IOptions{TOptions}"/>.</param>
    /// <param name="logger">The logger for diagnostic output.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    public AutoscalingOrchestrator(
        IWorkOrchestrator<TWork> inner,
        IWorkerRegistry registry,
        IWorkerMetrics metrics,
        IOptions<AutoscalingOptions> options,
        ILogger<AutoscalingOrchestrator<TWork>> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _inner = inner;
        _registry = registry;
        _metrics = metrics;
        _options = options.Value;
        _logger = logger;

        if (!_options.Enabled)
        {
            _logger.LogWarning("Autoscaling is disabled - decorator will pass through without metrics overhead");
        }
    }

    /// <inheritdoc/>
    public int PendingCount => _inner.PendingCount;

    /// <inheritdoc/>
    public int ActiveWorkers => _options.Enabled
        ? _inner.ActiveWorkers + _registry.ActiveWorkerCount
        : _inner.ActiveWorkers;

    /// <inheritdoc/>
    public int Capacity => _inner.Capacity;

    /// <inheritdoc/>
    public ChannelWriter<TWork> Writer => _inner.Writer;

    /// <inheritdoc/>
    public async ValueTask EnqueueAsync(TWork work, CancellationToken ct = default)
    {
        await _inner.EnqueueAsync(work, ct).ConfigureAwait(false);

        if (_options.Enabled)
        {
            _metrics.RecordEnqueue();
        }
    }

    /// <inheritdoc/>
    public bool TryEnqueue(TWork work)
    {
        var result = _inner.TryEnqueue(work);

        if (result && _options.Enabled)
        {
            _metrics.RecordEnqueue();
        }

        return result;
    }

    /// <summary>
    /// Requests the autoscaler to add workers.
    /// </summary>
    /// <param name="count">The number of workers to add.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// <para>
    /// Creates workers using the inner orchestrator's <see cref="IWorkOrchestrator{TWork}.CreateWorkerFunction(Action{bool}?)"/>
    /// which captures the real channel reader in its closure. A state callback is provided
    /// to track busy/idle status and record metrics.
    /// </para>
    /// <para>
    /// Workers are registered with the <see cref="IWorkerRegistry"/> and will be
    /// included in the <see cref="ActiveWorkers"/> count.
    /// </para>
    /// </remarks>
    public async Task RequestScaleUpAsync(int count, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return;
        }

        for (var i = 0; i < count; i++)
        {
            var workerId = $"AutoScale-{Guid.NewGuid():N}";

            // Create a state callback that marks busy/idle and records metrics
            void StateCallback(bool isBusy)
            {
                var workerInfo = _registry.GetWorkerInfo(workerId);
                if (workerInfo == null)
                {
                    return;
                }

                if (isBusy)
                {
                    workerInfo.MarkBusy();
                    _metrics.RecordExecutionStart();
                }
                else
                {
                    _metrics.RecordExecutionEnd();
                    workerInfo.MarkIdle();
                }
            }

            // Get a worker function from the inner orchestrator, which captures the real channel reader
            var workerFunc = _inner.CreateWorkerFunction(StateCallback);

            await _registry.CreateWorkerAsync(workerId, workerFunc, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Requests the autoscaler to remove workers.
    /// </summary>
    /// <param name="count">The number of workers to remove.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// Only removes idle workers to avoid interrupting in-progress work.
    /// The actual number of workers removed may be less than requested if
    /// fewer idle workers are available.
    /// </remarks>
    public Task RequestScaleDownAsync(int count, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return Task.CompletedTask;
        }

        // Only scale down idle workers
        var idleCount = _registry.IdleWorkerCount;
        var toStop = Math.Min(count, idleCount);

        _registry.RequestMultipleWorkerStop(toStop);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Run(TWork work)
    {
        _inner.Run(work);

        if (_options.Enabled)
        {
            _metrics.RecordEnqueue();
        }
    }

    /// <inheritdoc/>
    public bool TryRun(TWork work)
    {
        var result = _inner.TryRun(work);

        if (result && _options.Enabled)
        {
            _metrics.RecordEnqueue();
        }

        return result;
    }

    /// <inheritdoc/>
    public Func<string, CancellationToken, Task> CreateWorkerFunction()
        => _inner.CreateWorkerFunction();

    /// <inheritdoc/>
    public Func<string, CancellationToken, Task> CreateWorkerFunction(Action<bool>? stateCallback)
        => _inner.CreateWorkerFunction(stateCallback);

    /// <inheritdoc/>
    public CancellationToken GetShutdownToken()
        => _inner.GetShutdownToken();

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken ct = default)
    {
        return _inner.StopAsync(ct);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        return _inner.DisposeAsync();
    }
}
