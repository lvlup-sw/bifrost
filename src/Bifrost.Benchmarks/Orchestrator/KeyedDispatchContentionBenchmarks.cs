// =============================================================================
// <copyright file="KeyedDispatchContentionBenchmarks.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using BenchmarkDotNet.Attributes;

using Bifrost.Benchmarks.Helpers;
using Bifrost.Core;
using Bifrost.Queues;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bifrost.Benchmarks.Orchestrator;

/// <summary>
/// Measures keyed dispatch under contention: several producers enqueue at the same time while
/// several workers take items, compared with the FIFO path on the same load.
/// </summary>
/// <remarks>
/// <para>
/// Each invocation runs <see cref="Producers"/> producer tasks that together enqueue
/// <see cref="ItemCount"/> items with <c>TryEnqueue</c>, and waits until <see cref="Workers"/>
/// workers have handled all of them. The handler only signals a countdown, so the figure is the
/// dispatch overhead per item: admission, the queue lock, wake-ups, the key lease and its
/// release, and the terminal outcome stage.
/// </para>
/// <para>
/// <see cref="Mode"/> selects the load. <c>Fifo</c> is the existing FIFO path. <c>KeyedN</c> uses
/// keyed dispatch with N distinct keys and a per-key concurrency of 1. <c>Keyed1</c> is the hot-key
/// worst case: every item has the same key, so only one item runs at a time and the other workers
/// stay idle. <c>Keyed64</c> and <c>Keyed4096</c> spread the load so all workers stay busy, and
/// show the cost of the shared keyed lock with contending producers and workers. Per-key and
/// global limits are set high enough that no item is rejected.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class KeyedDispatchContentionBenchmarks
{
    private const int ItemCount = 4096;
    private const int Producers = 4;
    private const int Workers = 4;

    private WorkOrchestrator<int>? _orchestrator;
    private CountdownEvent? _countdown;

    /// <summary>Gets or sets the dispatch mode under test.</summary>
    [Params("Fifo", "Keyed1", "Keyed64", "Keyed4096")]
    public string Mode { get; set; } = "Fifo";

    /// <summary>Creates the orchestrator for <see cref="Mode"/> and warms it up with one batch.</summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        _countdown = new CountdownEvent(ItemCount);
        var handler = new CountdownWorkHandler(_countdown);
        var logger = NullLogger<WorkOrchestrator<int>>.Instance;
        if (Mode == "Fifo")
        {
            _orchestrator = new WorkOrchestrator<int>(handler, Options.Create(new WorkOrchestratorOptions
            {
                Capacity = 10000,
                WorkerCount = Workers,
            }), logger);
        }
        else
        {
            var keys = int.Parse(Mode.AsSpan("Keyed".Length), System.Globalization.CultureInfo.InvariantCulture);
            _orchestrator = new WorkOrchestrator<int>(
                handler,
                Options.Create(new WorkOrchestratorOptions
                {
                    Capacity = 10000,
                    WorkerCount = Workers,
                    FullMode = QueueFullMode.Reject,
                    DispatchStrategy = DispatchStrategy.Keyed,
                }),
                logger,
                null,
                null,
                (options, _) => KeyedWorkQueue.Create<int, int>(
                    item => item % keys, options.Capacity, options.Capacity, 1));
        }

        RunBatch();
    }

    /// <summary>Resets the countdown event before each iteration.</summary>
    [IterationSetup]
    public void IterationSetup() => _countdown!.Reset(ItemCount);

    /// <summary>Enqueues one batch from several producers and waits until every item was handled.</summary>
    [Benchmark(OperationsPerInvoke = ItemCount)]
    public void ContendedEnqueueAndDispatch() => RunBatch();

    /// <summary>Disposes the orchestrator.</summary>
    /// <returns>A task that completes when the orchestrator is disposed.</returns>
    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        if (_orchestrator is not null)
        {
            await _orchestrator.DisposeAsync();
        }

        _countdown?.Dispose();
    }

    private void RunBatch()
    {
        var orchestrator = _orchestrator!;
        Parallel.For(0, Producers, new ParallelOptions { MaxDegreeOfParallelism = Producers }, producer =>
        {
            for (var i = producer; i < ItemCount; i += Producers)
            {
                if (!orchestrator.TryEnqueue(i))
                {
                    throw new InvalidOperationException($"Item {i} was rejected.");
                }
            }
        });

        if (!_countdown!.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("Workers did not drain all items within the timeout.");
        }
    }
}
