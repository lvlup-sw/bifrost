// =============================================================================
// <copyright file="WorkerLoopAllocationBenchmarks.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using BenchmarkDotNet.Attributes;
using Bifrost.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bifrost.Benchmarks.Allocation;

/// <summary>
/// Validates steady-state worker loop allocates nothing per item.
/// </summary>
/// <remarks>
/// After JIT and async state machines are warmed up, the worker loop
/// processing items from the channel should produce zero allocations
/// per item in steady state.
/// </remarks>
[MemoryDiagnoser]
public class WorkerLoopAllocationBenchmarks
{
    private const int ItemCount = 1000;

    private WorkOrchestrator<int>? _orchestrator;
    private CountdownEvent? _countdown;

    /// <summary>
    /// Creates the orchestrator with a handler that signals a countdown event,
    /// then warms up with 1000 items to stabilize JIT and async state machines.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        _countdown = new CountdownEvent(ItemCount);
        var handler = new CountdownHandler(_countdown);
        var options = Options.Create(new WorkOrchestratorOptions { Capacity = 10000, WorkerCount = 1 });
        var logger = NullLogger<WorkOrchestrator<int>>.Instance;

        _orchestrator = new WorkOrchestrator<int>(handler, options, logger);

        // Warmup: enqueue 1000 items and wait for all to be processed
        // to stabilize JIT and async state machines.
        for (var i = 0; i < ItemCount; i++)
        {
            _orchestrator.TryEnqueue(i);
        }

        _countdown.Wait(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Resets the countdown event before each benchmark iteration.
    /// </summary>
    [IterationSetup]
    public void IterationSetup()
    {
        _countdown!.Reset(ItemCount);
    }

    /// <summary>
    /// Enqueues 1000 items and waits for all to be processed.
    /// Measures per-item allocation in steady state.
    /// </summary>
    [Benchmark(OperationsPerInvoke = ItemCount)]
    public void WorkerLoop_SteadyState()
    {
        for (var i = 0; i < ItemCount; i++)
        {
            _orchestrator!.TryEnqueue(i);
        }

        _countdown!.Wait(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Disposes the orchestrator and releases resources.
    /// </summary>
    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        if (_orchestrator is not null)
        {
            await _orchestrator.DisposeAsync().ConfigureAwait(false);
        }

        _countdown?.Dispose();
    }

    private sealed class CountdownHandler(CountdownEvent countdown) : IWorkHandler<int>
    {
        public ValueTask HandleAsync(int work, CancellationToken ct)
        {
            countdown.Signal();
            return ValueTask.CompletedTask;
        }
    }
}
