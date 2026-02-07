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
/// Quantifies event object allocation cost when published through the event stream.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="EventStreamOrchestrator{TWork}"/> publishes <see cref="WorkEnqueuedEvent{TWork}"/>
/// (a sealed record class) to subscriber channels of type <c>Channel&lt;IOrchestratorEvent&gt;</c>.
/// Because events are reference types, no boxing occurs at the <c>TryWrite(evt)</c> call site.
/// </para>
/// <para>
/// This benchmark measures the allocation cost of creating the event record instance itself.
/// The result quantifies the allocation price of event streaming compared to the zero-allocation
/// base orchestrator path.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class EventStreamAllocationBenchmarks
{
    private WorkOrchestrator<int>? _innerOrchestrator;
    private EventStreamOrchestrator<int>? _eventStreamOrchestrator;
    private CancellationTokenSource? _subscriberCts;
    private Task? _subscriberTask;

    /// <summary>
    /// Creates the base orchestrator, wraps it with the event stream decorator,
    /// and registers a subscriber to ensure the broadcast path is exercised.
    /// </summary>
    [GlobalSetup]
    public async Task GlobalSetup()
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
        _subscriberTask = Task.Run(
            async () =>
            {
                await foreach (var evt in _eventStreamOrchestrator.GetEventStreamAsync<WorkEnqueuedEvent<int>>(
                    cancellationToken: _subscriberCts.Token))
                {
                    // Drain events to prevent channel backpressure
                }
            },
            _subscriberCts.Token);

        // Allow subscriber channel registration to complete. This is an async operation
        // that is near-instant, but Thread.Sleep blocks the setup thread unnecessarily.
        // Using async Task.Delay allows BenchmarkDotNet to handle the wait properly.
        await Task.Delay(100).ConfigureAwait(false);
    }

    /// <summary>
    /// Enqueues an item through the event stream orchestrator, which creates a
    /// <see cref="WorkEnqueuedEvent{TWork}"/> record instance and publishes it to the subscriber channel.
    /// Allocation comes from creating the event object; no boxing occurs since events are reference types.
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

        if (_subscriberTask is not null)
        {
            try
            {
#pragma warning disable VSTHRD003 // Awaiting task started in GlobalSetup for orderly cleanup
                await _subscriberTask.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_eventStreamOrchestrator is not null)
        {
            await _eventStreamOrchestrator.DisposeAsync().ConfigureAwait(false);
        }
    }
}