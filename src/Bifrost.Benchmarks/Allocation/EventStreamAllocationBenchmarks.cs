// =============================================================================
// <copyright file="EventStreamAllocationBenchmarks.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using BenchmarkDotNet.Attributes;
using Bifrost.Benchmarks.Helpers;
using Bifrost.Core;
using Bifrost.Core.Events;
using Bifrost.Decorators;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bifrost.Benchmarks.Allocation;

/// <summary>
/// Quantifies event struct boxing cost when published through the event stream.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="EventStreamOrchestrator{TWork}"/> publishes <see cref="WorkEnqueuedEvent{TWork}"/>
/// (a readonly record struct) to subscriber channels of type <c>Channel&lt;IOrchestratorEvent&gt;</c>.
/// The <c>TryWrite(evt)</c> call boxes the struct since it is cast to the <c>IOrchestratorEvent</c>
/// interface.
/// </para>
/// <para>
/// This benchmark intentionally reveals that boxing cost. The result quantifies the allocation
/// price of event streaming compared to the zero-allocation base orchestrator path.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class EventStreamAllocationBenchmarks
{
    private WorkOrchestrator<int>? _innerOrchestrator;
    private EventStreamOrchestrator<int>? _eventStreamOrchestrator;
    private CancellationTokenSource? _subscriberCts;

    /// <summary>
    /// Creates the base orchestrator, wraps it with the event stream decorator,
    /// and registers a subscriber to ensure the broadcast path is exercised.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        var handler = new NoOpWorkHandler();
        var options = Options.Create(new WorkOrchestratorOptions { Capacity = 10000, WorkerCount = 1 });
        var innerLogger = NullLogger<WorkOrchestrator<int>>.Instance;
        var eventLogger = NullLogger<EventStreamOrchestrator<int>>.Instance;

        _innerOrchestrator = new WorkOrchestrator<int>(handler, options, innerLogger);
        _eventStreamOrchestrator = new EventStreamOrchestrator<int>(_innerOrchestrator, eventLogger);

        // Start a background subscriber to register a subscriber channel.
        // This ensures PublishToSubscribers iterates at least one subscriber.
        _subscriberCts = new CancellationTokenSource();
        _ = Task.Run(
            async () =>
            {
                await foreach (var evt in _eventStreamOrchestrator.GetEventStreamAsync<WorkEnqueuedEvent<int>>(
                    cancellationToken: _subscriberCts.Token))
                {
                    // Drain events to prevent channel backpressure
                }
            },
            _subscriberCts.Token);

        // Allow subscriber registration to complete
        Thread.Sleep(50);
    }

    /// <summary>
    /// Enqueues an item through the event stream orchestrator, which creates a
    /// <see cref="WorkEnqueuedEvent{TWork}"/> struct and publishes it to the subscriber channel.
    /// The boxing occurs at <c>TryWrite(evt)</c> because the channel is <c>Channel&lt;IOrchestratorEvent&gt;</c>.
    /// </summary>
    /// <returns><c>true</c> if the item was enqueued successfully.</returns>
    [Benchmark]
    public bool TryEnqueue_WithEventStream()
    {
        return _eventStreamOrchestrator!.TryEnqueue(42);
    }

    /// <summary>
    /// Disposes the orchestrators and cancels the subscriber.
    /// </summary>
    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        if (_subscriberCts is not null)
        {
            await _subscriberCts.CancelAsync().ConfigureAwait(false);
            _subscriberCts.Dispose();
        }

        if (_eventStreamOrchestrator is not null)
        {
            await _eventStreamOrchestrator.DisposeAsync().ConfigureAwait(false);
        }
    }
}
