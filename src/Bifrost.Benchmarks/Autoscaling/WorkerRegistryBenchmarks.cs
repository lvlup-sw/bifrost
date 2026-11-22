// =============================================================================
// <copyright file="WorkerRegistryBenchmarks.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using BenchmarkDotNet.Attributes;

using Bifrost.Autoscaling;

namespace Bifrost.Benchmarks.Autoscaling;

/// <summary>
/// Measures worker registry operations: enumeration and property access.
/// </summary>
[MemoryDiagnoser]
public class WorkerRegistryBenchmarks
{
    private WorkerRegistry? _registry;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Creates a worker registry pre-populated with workers.
    /// </summary>
    /// <remarks>
    /// The setup registers 8 workers with a long-running function that stays alive. It marks half
    /// of them as busy to exercise the filtering of <c>IdleWorkerCount</c>.
    /// </remarks>
    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _registry = new WorkerRegistry();
        _cts = new CancellationTokenSource();

        for (var i = 0; i < 8; i++)
        {
            var workerId = $"worker-{i}";
            await _registry.CreateWorkerAsync(
                workerId,
                static (_, ct) => Task.Delay(Timeout.Infinite, ct),
                _cts.Token).ConfigureAwait(false);

            if (i % 2 == 0)
            {
                _registry.GetWorkerInfo(workerId)?.MarkBusy();
            }
        }
    }

    /// <summary>
    /// Cancels and disposes long-running worker tasks.
    /// </summary>
    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    /// <summary>
    /// Benchmarks enumerating all registered workers.
    /// </summary>
    /// <returns>A read-only collection of all worker information.</returns>
    [Benchmark]
    public IReadOnlyCollection<WorkerInfo> GetAllWorkers()
    {
        return _registry!.GetAllWorkers();
    }

    /// <summary>
    /// Benchmarks the ActiveWorkerCount property access.
    /// </summary>
    /// <returns>The active worker count.</returns>
    [Benchmark]
    public int ActiveWorkerCount() => _registry!.ActiveWorkerCount;

    /// <summary>
    /// Benchmarks the IdleWorkerCount property access (involves LINQ filtering).
    /// </summary>
    /// <returns>The idle worker count.</returns>
    [Benchmark]
    public int IdleWorkerCount() => _registry!.IdleWorkerCount;
}
