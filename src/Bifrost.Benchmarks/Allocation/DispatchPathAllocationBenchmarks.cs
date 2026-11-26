// =============================================================================
// <copyright file="DispatchPathAllocationBenchmarks.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using BenchmarkDotNet.Attributes;

using Bifrost.Benchmarks.Helpers;
using Bifrost.Core;
using Bifrost.Queues;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bifrost.Benchmarks.Allocation;

/// <summary>
/// Measures the steady-state allocation per item of the full enqueue → dispatch → handler path
/// for each dispatch strategy: the existing FIFO and exact-priority paths, and keyed dispatch.
/// </summary>
/// <remarks>
/// <para>
/// One producer and one worker. Each invocation enqueues <see cref="ItemCount"/> items with
/// <c>TryEnqueue</c> and waits until the handler has seen all of them, so the figure includes
/// admission, the queue, the worker loop, the terminal outcome stage, and, for keyed dispatch,
/// the key lease and its release. It mirrors <see cref="WorkerLoopAllocationBenchmarks"/>, which
/// covers the FIFO path only.
/// </para>
/// <para>
/// The 0.7.0 release check uses it to confirm that the existing FIFO and priority paths stay
/// allocation-free per item after the keyed dispatch changes, and to record the keyed cost. Keyed
/// items use <see cref="KeyCount"/> distinct keys with a per-key concurrency of 1, so keys are
/// created and removed as they drain.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class DispatchPathAllocationBenchmarks
{
    private const int ItemCount = 1000;
    private const int KeyCount = 64;

    private WorkOrchestrator<int>? _orchestrator;
    private CountdownEvent? _countdown;

    /// <summary>Gets or sets the dispatch strategy under test.</summary>
    [Params("Fifo", "PriorityExact", "Keyed")]
    public string Strategy { get; set; } = "Fifo";

    /// <summary>Creates the orchestrator for <see cref="Strategy"/> and warms it up with one batch.</summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        _countdown = new CountdownEvent(ItemCount);
        var handler = new CountdownWorkHandler(_countdown);
        var logger = NullLogger<WorkOrchestrator<int>>.Instance;
        _orchestrator = Strategy switch
        {
            "Fifo" => new WorkOrchestrator<int>(handler, Options.Create(new WorkOrchestratorOptions
            {
                Capacity = 10000,
                WorkerCount = 1,
            }), logger),
            "PriorityExact" => new WorkOrchestrator<int>(handler, Options.Create(new WorkOrchestratorOptions
            {
                Capacity = 10000,
                WorkerCount = 1,
                FullMode = QueueFullMode.Reject,
                DispatchStrategy = DispatchStrategy.PriorityExact,
            }), logger),
            "Keyed" => new WorkOrchestrator<int>(
                handler,
                Options.Create(new WorkOrchestratorOptions
                {
                    Capacity = 10000,
                    WorkerCount = 1,
                    FullMode = QueueFullMode.Reject,
                    DispatchStrategy = DispatchStrategy.Keyed,
                }),
                logger,
                null,
                null,
                static (options, _) => KeyedWorkQueue.Create<int, int>(
                    static item => item % KeyCount, options.Capacity, options.Capacity, 1)),
            _ => throw new InvalidOperationException($"Unknown strategy '{Strategy}'."),
        };

        RunBatch();
    }

    /// <summary>Resets the countdown event before each iteration.</summary>
    [IterationSetup]
    public void IterationSetup() => _countdown!.Reset(ItemCount);

    /// <summary>Enqueues one batch and waits until every item was handled.</summary>
    [Benchmark(OperationsPerInvoke = ItemCount)]
    public void EnqueueAndDispatch_SteadyState() => RunBatch();

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
        for (var i = 0; i < ItemCount; i++)
        {
            if (!_orchestrator!.TryEnqueue(i))
            {
                throw new InvalidOperationException($"Item {i} was rejected.");
            }
        }

        if (!_countdown!.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("Workers did not drain all items within the timeout.");
        }
    }
}
