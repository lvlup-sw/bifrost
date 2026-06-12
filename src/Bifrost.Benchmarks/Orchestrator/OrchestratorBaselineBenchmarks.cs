// =============================================================================
// <copyright file="OrchestratorBaselineBenchmarks.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using BenchmarkDotNet.Attributes;

using Bifrost.Benchmarks.Helpers;
using Bifrost.Core;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bifrost.Benchmarks.Orchestrator;

/// <summary>
/// FIFO baseline benchmarks for the current <see cref="WorkOrchestrator{TWork}"/>
/// (bounded <see cref="System.Threading.Channels.Channel{T}"/>, <c>FullMode.Wait</c>).
/// </summary>
/// <remarks>
/// <para>
/// Captures the DR-7 reference numbers for the CPQ port + priority dispatch feature:
/// the enqueue-to-dispatch round-trip cost of pushing <see cref="ItemCount"/> items
/// through <see cref="WorkOrchestrator{TWork}.EnqueueAsync"/> and waiting for every
/// handler invocation to complete.
/// </para>
/// <para>
/// The post-port comparison run (T28) MUST use identical job settings so the
/// numbers are directly comparable. Baseline capture used <c>--job Short</c>
/// (1 launch, 3 warmup iterations, 3 measured iterations).
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class OrchestratorBaselineBenchmarks
{
    private const int ItemCount = 10_000;
    private const int ChannelCapacity = 128;

    private WorkOrchestrator<int>? _orchestrator;
    private CountdownEvent? _countdown;

    /// <summary>
    /// Gets or sets the number of concurrent workers processing items.
    /// </summary>
    [Params(1, 2, 8)]
    public int WorkerCount { get; set; }

    /// <summary>
    /// Creates the orchestrator with a no-op handler that signals a countdown event.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        _countdown = new CountdownEvent(ItemCount);

        var handler = new CountdownWorkHandler(_countdown);
        var options = Options.Create(new WorkOrchestratorOptions
        {
            Capacity = ChannelCapacity,
            WorkerCount = WorkerCount,
        });
        var logger = NullLogger<WorkOrchestrator<int>>.Instance;

        _orchestrator = new WorkOrchestrator<int>(handler, options, logger);
    }

    /// <summary>
    /// Resets the countdown event before each iteration.
    /// </summary>
    [IterationSetup]
    public void IterationSetup()
    {
        _countdown!.Reset(ItemCount);
    }

    /// <summary>
    /// Enqueues <see cref="ItemCount"/> items through <see cref="WorkOrchestrator{TWork}.EnqueueAsync"/>
    /// and waits for all handler invocations to complete (full round-trip).
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the benchmark operation.</returns>
    [Benchmark]
    public async Task EnqueueDispatchRoundTrip()
    {
        for (var i = 0; i < ItemCount; i++)
        {
            await _orchestrator!.EnqueueAsync(i).ConfigureAwait(false);
        }

        if (!_countdown!.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("Workers did not process all items within timeout.");
        }
    }

    /// <summary>
    /// Disposes the orchestrator and countdown event after all benchmarks complete.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> representing the cleanup operation.</returns>
    [GlobalCleanup]
    public async ValueTask GlobalCleanup()
    {
        if (_orchestrator is not null)
        {
            await _orchestrator.DisposeAsync().ConfigureAwait(false);
        }

        _countdown?.Dispose();
    }
}
