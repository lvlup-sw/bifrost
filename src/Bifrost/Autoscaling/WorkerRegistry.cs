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
    public Task<WorkerInfo> CreateWorkerAsync(
        string workerId,
        Func<string, CancellationToken, Task> workerFunction,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workerId);
        ArgumentNullException.ThrowIfNull(workerFunction);

        var workerInfo = new WorkerInfo(workerId);

        if (!_workers.TryAdd(workerId, workerInfo))
        {
            throw new InvalidOperationException($"Worker with ID '{workerId}' already exists.");
        }

        // Start the worker task
        // Note: Pass CancellationToken.None to StartNew so the delegate is always scheduled.
        // Check cancellation inside the delegate to ensure cleanup always runs.
        var workerTask = Task.Factory.StartNew(
            async () =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    await workerFunction(workerId, ct).ConfigureAwait(false);
                }
                finally
                {
                    _workers.TryRemove(workerId, out _);
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        // Observe faults to prevent unobserved task exceptions and log errors
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
    public int RequestMultipleWorkerStop(int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        // Get candidates that haven't been stopped yet
        var candidates = _workers.Values
            .Where(w => !w.StopRequested)
            .ToList();

        // Sort: idle workers first, then by creation time descending (LIFO - newest first)
        var sorted = candidates
            .OrderByDescending(w => w.IsIdle)
            .ThenByDescending(w => w.CreatedAt)
            .Take(count)
            .ToList();

        var stoppedCount = 0;
        foreach (var worker in sorted)
        {
            worker.RequestStop();
            stoppedCount++;
        }

        return stoppedCount;
    }

    /// <inheritdoc/>
    public WorkerRegistrySnapshot GetSnapshot()
    {
        var workers = _workers.Values.ToList();

        var total = workers.Count;
        var active = total; // All workers in registry are considered active
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