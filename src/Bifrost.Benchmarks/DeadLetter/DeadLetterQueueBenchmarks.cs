// =============================================================================
// <copyright file="DeadLetterQueueBenchmarks.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using BenchmarkDotNet.Attributes;

using Bifrost.Core.DeadLetter;
using Bifrost.DeadLetter;
using Bifrost.Outcomes;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bifrost.Benchmarks.DeadLetter;

/// <summary>
/// Benchmarks for dead letter queue operations.
/// </summary>
[MemoryDiagnoser]
public class DeadLetterQueueBenchmarks
{
    private DeadLetterQueue<int>? _dlq;
    private DeadLetterQueue<int>? _drainDlq;
    private IOptions<DeadLetterQueueOptions>? _options;
    private TerminalOutcomeStage<int>? _stage;
    private DeadLetteredWork<int> _testItem;
    private InvalidOperationException? _failure;

    /// <summary>
    /// Sets up the benchmarks.
    /// </summary>
    /// <remarks>
    /// The terminal outcome stage owns dead-letter capture. The happy path settles a success. The
    /// failure path settles a final failure and captures it in a queue with spare capacity.
    /// </remarks>
    [GlobalSetup]
    public Task GlobalSetup()
    {
        _options = Options.Create(new DeadLetterQueueOptions { Capacity = 10000, MaxRetries = 3 });

        _dlq = new DeadLetterQueue<int>(_options, NullLogger<DeadLetterQueue<int>>.Instance);
        _drainDlq = new DeadLetterQueue<int>(_options, NullLogger<DeadLetterQueue<int>>.Instance);

        _testItem = new DeadLetteredWork<int>(42, null, 1, DateTimeOffset.UtcNow, null);

        _failure = new InvalidOperationException("bench-fail");
        _stage = new TerminalOutcomeStage<int>(
            new TerminalOutcomeOptions<int>
            {
                DeadLetterQueue = new DeadLetterQueue<int>(_options, NullLogger<DeadLetterQueue<int>>.Instance),
                Notifier = new DeadLetterNotifier<int>(NullLogger<DeadLetterNotifier<int>>.Instance),
            },
            NullLogger.Instance,
            TimeProvider.System);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Benchmarks enqueueing to the dead letter queue.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
    [Benchmark]
    public ValueTask DlqEnqueue()
    {
        return _dlq!.EnqueueAsync(_testItem);
    }

    /// <summary>
    /// Pre-populates the drain DLQ before each iteration.
    /// </summary>
    /// <remarks>
    /// The method creates a new queue each time, so every iteration starts from a clean state.
    /// </remarks>
#pragma warning disable VSTHRD002 // IterationSetup must return void
    [IterationSetup(Target = nameof(DlqDrain))]
    public void SetupDrainIteration()
    {
        _drainDlq = new DeadLetterQueue<int>(_options!, NullLogger<DeadLetterQueue<int>>.Instance);
        for (var i = 0; i < 100; i++)
        {
            _drainDlq.EnqueueAsync(new DeadLetteredWork<int>(i, null, 1, DateTimeOffset.UtcNow, null))
                .AsTask().GetAwaiter().GetResult();
        }
    }
#pragma warning restore VSTHRD002

    /// <summary>
    /// Benchmarks draining items via ReadAllAsync.
    /// </summary>
    /// <returns>The number of items drained.</returns>
    [Benchmark]
    public async Task<int> DlqDrain()
    {
        var count = 0;
        await foreach (var _ in _drainDlq!.ReadAllAsync())
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Benchmarks settling a success in the terminal outcome stage (no capture).
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
    [Benchmark]
    public ValueTask HappyPath_NoFailure()
    {
        return _stage!.SettleAsync(42, TerminalOutcome.Succeeded, 1, null, TimeSpan.Zero);
    }

    /// <summary>
    /// Benchmarks settling a final failure in the terminal outcome stage (one capture).
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
    [Benchmark]
    public ValueTask FailurePath_DeadLetter()
    {
        return _stage!.SettleAsync(42, TerminalOutcome.Failed, 1, _failure, TimeSpan.Zero);
    }

    /// <summary>
    /// Cleans up the benchmarks.
    /// </summary>
    /// <remarks>
    /// This method does nothing, because the benchmarks hold no resources to clean up.
    /// </remarks>
    [GlobalCleanup]
    public void GlobalCleanup()
    {
    }
}
