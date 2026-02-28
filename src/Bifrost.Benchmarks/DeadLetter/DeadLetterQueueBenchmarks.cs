// =============================================================================
// <copyright file="DeadLetterQueueBenchmarks.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using BenchmarkDotNet.Attributes;

using Bifrost.Core;
using Bifrost.Core.DeadLetter;
using Bifrost.Core.Events;
using Bifrost.DeadLetter;

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
    private DeadLetterHandler<int>? _happyPathHandler;
    private DeadLetterHandler<int>? _failurePathHandler;
    private DeadLetteredWork<int> _testItem;

    /// <summary>
    /// Sets up the benchmarks.
    /// </summary>
    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _options = Options.Create(new DeadLetterQueueOptions { Capacity = 10000, MaxRetries = 3 });

        _dlq = new DeadLetterQueue<int>(_options);
        _drainDlq = new DeadLetterQueue<int>(_options);

        _testItem = new DeadLetteredWork<int>(42, null, 1, DateTimeOffset.UtcNow, null);

        var noopNotifier = new DeadLetterNotifier<int>();
        var logger = NullLogger<DeadLetterHandler<int>>.Instance;

        // Happy path handler - inner handler succeeds
        var succeedingHandler = new DelegateHandler(_ => ValueTask.CompletedTask);
        _happyPathHandler = new DeadLetterHandler<int>(
            succeedingHandler, new DeadLetterQueue<int>(_options), noopNotifier, _options, logger);

        // Failure path handler - inner handler always throws
        var failingHandler = new DelegateHandler(_ =>
            new ValueTask(Task.FromException(new InvalidOperationException("bench-fail"))));
        _failurePathHandler = new DeadLetterHandler<int>(
            failingHandler, new DeadLetterQueue<int>(_options), noopNotifier,
            Options.Create(new DeadLetterQueueOptions { Capacity = 10000, MaxRetries = 0 }), logger);
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
#pragma warning disable VSTHRD002 // IterationSetup must return void
    [IterationSetup(Target = nameof(DlqDrain))]
    public void SetupDrainIteration()
    {
        // Recreate to ensure clean state
        _drainDlq = new DeadLetterQueue<int>(_options!);
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
        await foreach (var _ in _drainDlq!.ReadAllAsync().ConfigureAwait(false))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Benchmarks the happy path through DeadLetterHandler (no failure, no retries).
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
    [Benchmark]
    public ValueTask HappyPath_NoFailure()
    {
        return _happyPathHandler!.HandleAsync(42, CancellationToken.None);
    }

    /// <summary>
    /// Benchmarks the failure path through DeadLetterHandler (immediate dead-letter).
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> representing the operation.</returns>
    [Benchmark]
    public ValueTask FailurePath_DeadLetter()
    {
        return _failurePathHandler!.HandleAsync(42, CancellationToken.None);
    }

    /// <summary>
    /// Cleans up the benchmarks.
    /// </summary>
    [GlobalCleanup]
    public void GlobalCleanup()
    {
        // No resources to clean up
    }

    private sealed class DelegateHandler(Func<int, ValueTask> handler) : IWorkHandler<int>
    {
        public ValueTask HandleAsync(int work, CancellationToken ct) => handler(work);
    }
}
