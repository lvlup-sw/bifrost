// =============================================================================
// <copyright file="WorkerRegistry.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bifrost.Autoscaling;

/// <summary>
/// Thread-safe implementation of <see cref="IWorkerRegistry"/> for managing worker instances.
/// </summary>
/// <remarks>
/// <para>
/// This implementation uses a <see cref="ConcurrentDictionary{TKey,TValue}"/> for thread-safe
/// worker management, allowing concurrent read and write operations.
/// </para>
/// <para>
/// When selecting workers to stop, idle workers are prioritized over busy workers,
/// and newer workers (by creation time) are stopped before older ones (LIFO).
/// </para>
/// </remarks>
public sealed class WorkerRegistry : IWorkerRegistry
{
    private readonly ConcurrentDictionary<string, WorkerInfo> _workers = new();
    private readonly ILogger<WorkerRegistry> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkerRegistry"/> class.
    /// </summary>
    /// <param name="logger">The logger instance for diagnostic output.</param>
    public WorkerRegistry(ILogger<WorkerRegistry> logger)
    {
        _logger = logger ?? NullLogger<WorkerRegistry>.Instance;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkerRegistry"/> class with no logger.
    /// </summary>
    public WorkerRegistry()
        : this(NullLogger<WorkerRegistry>.Instance)
    {
    }

    /// <inheritdoc/>
    public int ActiveWorkerCount => _workers.Count;

    /// <inheritdoc/>
    public int IdleWorkerCount => _workers.Values.Count(w => w.IsIdle && !w.StopRequested);

    /// <inheritdoc/>
    public IReadOnlyCollection<WorkerInfo> GetAllWorkers()
    {
        return _workers.Values.ToList().AsReadOnly();
    }

    /// <inheritdoc/>
    public WorkerInfo? GetWorkerInfo(string workerId)
    {
        return _workers.TryGetValue(workerId, out var workerInfo) ? workerInfo : null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The method passes <see cref="CancellationToken.None"/> to <c>StartNew</c>, so the delegate is
    /// always scheduled. The delegate checks the cancellation itself, so the cleanup in its
    /// <c>finally</c> block always runs. A fault-only continuation observes the worker task, which
    /// prevents unobserved task exceptions and logs the error.
    /// </remarks>
    public Task<WorkerInfo> CreateWorkerAsync(
        string workerId,
        Func<string, CancellationToken, Task> workerFunction,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workerId);
        ArgumentNullException.ThrowIfNull(workerFunction);
        ct.ThrowIfCancellationRequested();

        var workerInfo = new WorkerInfo(workerId);

        if (!_workers.TryAdd(workerId, workerInfo))
        {
            workerInfo.DisposeStopSource();
            throw new InvalidOperationException($"Worker with ID '{workerId}' already exists.");
        }

        var workerTask = Task.Factory.StartNew(
            async () =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    await workerFunction(workerId, ct);
                }
                finally
                {
                    workerInfo.DisposeStopSource();
                    _workers.TryRemove(workerId, out _);
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        _ = workerTask.ContinueWith(
            t => _logger.LogError(t.Exception, "Worker {WorkerId} faulted", workerId),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

        return Task.FromResult(workerInfo);
    }

    /// <inheritdoc/>
    public bool RequestWorkerStop(string workerId)
    {
        if (_workers.TryGetValue(workerId, out var workerInfo))
        {
            workerInfo.RequestStop();
            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The candidates are the workers without a stop request. Idle workers come first, then the
    /// order is by creation time, descending (LIFO: newest first).
    /// </remarks>
    public int RequestMultipleWorkerStop(int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        var candidates = _workers.Values
            .Where(w => !w.StopRequested)
            .ToList();

        var sorted = candidates
            .OrderByDescending(w => w.IsIdle)
            .ThenByDescending(w => w.CreatedAt)
            .Take(count)
            .ToList();

        var stoppedCount = 0;
        foreach (var worker in sorted)
        {
            if (worker.RequestStop()) stoppedCount++;
        }

        return stoppedCount;
    }

    /// <summary>
    /// Atomically requests up to <paramref name="count"/> currently-idle workers to
    /// stop (newest first), skipping any worker that transitions to busy concurrently.
    /// </summary>
    /// <param name="count">The maximum number of idle workers to stop.</param>
    /// <returns>The actual number of idle workers claimed for stop.</returns>
    internal int RequestIdleWorkersStop(int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        var candidates = _workers.Values
            .Where(w => w.IsIdle && !w.StopRequested)
            .OrderByDescending(w => w.CreatedAt)
            .ToList();

        var stoppedCount = 0;
        foreach (var worker in candidates)
        {
            if (stoppedCount >= count)
            {
                break;
            }

            if (worker.TryRequestStopIfIdle())
            {
                stoppedCount++;
            }
        }

        return stoppedCount;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// All workers in the registry count as active.
    /// </remarks>
    public WorkerRegistrySnapshot GetSnapshot()
    {
        var workers = _workers.Values.ToList();

        var total = workers.Count;
        var active = total;
        var idle = workers.Count(w => w.IsIdle && !w.StopRequested);
        var stopping = workers.Count(w => w.StopRequested);
        var busy = workers.Count(w => !w.IsIdle && !w.StopRequested);

        return new WorkerRegistrySnapshot(
            TotalWorkers: total,
            ActiveWorkers: active,
            IdleWorkers: idle,
            BusyWorkers: busy,
            StoppingWorkers: stopping,
            Timestamp: DateTimeOffset.UtcNow);
    }
}