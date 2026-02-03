// =============================================================================
// <copyright file="WorkOrchestratorTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Threading.Channels;
using Bifrost.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Bifrost.Tests;

/// <summary>
/// A work handler that blocks until released, useful for deterministic testing.
/// </summary>
/// <remarks>
/// Workers will block in HandleAsync until ReleaseAll is called,
/// preventing the channel from being drained during test assertions.
/// This enables precise control over worker timing in channel capacity tests.
/// </remarks>
internal sealed class BlockingWorkHandler : IWorkHandler<string>, IDisposable
{
    private readonly ManualResetEventSlim _releaseSignal = new(initialState: false);
    private int _handleCallCount;

    /// <summary>
    /// Gets the number of times HandleAsync has been entered.
    /// </summary>
    /// <remarks>Uses volatile read for thread-safe access to the call count.</remarks>
    public int HandleCallCount => Volatile.Read(ref _handleCallCount);

    /// <summary>
    /// Handles work by blocking until released or cancelled.
    /// </summary>
    /// <param name="work">The work item to handle.</param>
    /// <param name="ct">Cancellation token to observe.</param>
    /// <returns>A ValueTask that completes when released or cancelled.</returns>
    /// <remarks>Increments the call count atomically before blocking on the release signal.</remarks>
    public ValueTask HandleAsync(string work, CancellationToken ct)
    {
        Interlocked.Increment(ref _handleCallCount);
        _releaseSignal.Wait(ct);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Releases all blocked handlers by signaling the reset event.
    /// </summary>
    /// <remarks>Sets the manual reset event to allow all waiting workers to proceed.</remarks>
    public void ReleaseAll()
    {
        _releaseSignal.Set();
    }

    /// <summary>
    /// Disposes the blocking handler and releases any waiting workers.
    /// </summary>
    /// <remarks>Signals release before disposing to prevent worker deadlock during cleanup.</remarks>
    public void Dispose()
    {
        _releaseSignal.Set(); // Ensure workers can exit
        _releaseSignal.Dispose();
    }
}

/// <summary>
/// Unit tests for the <see cref="WorkOrchestrator{TWork}"/> class.
/// </summary>
/// <remarks>
/// Tests cover construction, enqueueing, worker processing, exception handling,
/// queue depth tracking, graceful shutdown, and resource disposal.
/// </remarks>
public class WorkOrchestratorTests
{
    private IWorkHandler<string> _handler = null!;
    private IOptions<WorkOrchestratorOptions> _options = null!;
    private ILogger<WorkOrchestrator<string>> _logger = null!;

    /// <summary>
    /// Sets up test dependencies before each test.
    /// </summary>
    /// <returns>A Task representing the async setup operation.</returns>
    /// <remarks>Creates mock handler, default options, and mock logger for test isolation.</remarks>
    [Before(Test)]
    public Task Setup()
    {
        _handler = Substitute.For<IWorkHandler<string>>();
        _options = Options.Create(new WorkOrchestratorOptions());
        _logger = Substitute.For<ILogger<WorkOrchestrator<string>>>();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies that the constructor properly initializes the channel with default options.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges default options, acts by constructing an orchestrator, asserts default capacity,
    /// worker count, and zero pending items.
    /// </remarks>
    [Test]
    public async Task WorkOrchestrator_Constructor_CreatesChannel()
    {
        // Arrange & Act
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);

        // Assert
        await Assert.That(orchestrator.Capacity).IsEqualTo(128);
        await Assert.That(orchestrator.ActiveWorkers).IsEqualTo(2);
        await Assert.That(orchestrator.PendingCount).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies that the constructor throws ArgumentNullException when handler is null.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Acts by constructing with null handler, asserts ArgumentNullException is thrown.
    /// Guard clause validation ensures required dependencies are provided.
    /// </remarks>
    [Test]
    public async Task WorkOrchestrator_Constructor_ThrowsWhenHandlerNull()
    {
        // Arrange & Act & Assert
        await Assert.That(() => new WorkOrchestrator<string>(null!, _options, _logger))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that the constructor throws ArgumentNullException when options is null.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Acts by constructing with null options, asserts ArgumentNullException is thrown.
    /// Guard clause validation ensures required dependencies are provided.
    /// </remarks>
    [Test]
    public async Task WorkOrchestrator_Constructor_ThrowsWhenOptionsNull()
    {
        // Arrange & Act & Assert
        await Assert.That(() => new WorkOrchestrator<string>(_handler, null!, _logger))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that the constructor throws ArgumentNullException when logger is null.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Acts by constructing with null logger, asserts ArgumentNullException is thrown.
    /// Guard clause validation ensures required dependencies are provided.
    /// </remarks>
    [Test]
    public async Task WorkOrchestrator_Constructor_ThrowsWhenLoggerNull()
    {
        // Arrange & Act & Assert
        await Assert.That(() => new WorkOrchestrator<string>(_handler, _options, null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that EnqueueAsync writes work to the channel and workers process it.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges an orchestrator with mock handler that signals completion, acts by enqueueing work,
    /// asserts the handler received the work item.
    /// </remarks>
    [Test]
    public async Task EnqueueAsync_ValidWork_WritesToChannel()
    {
        // Arrange
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);
        var tcs = new TaskCompletionSource();
        _handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                tcs.SetResult();
                return ValueTask.CompletedTask;
            });

        // Act
        await orchestrator.EnqueueAsync("test-work").ConfigureAwait(false);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        // Assert
        await _handler.Received(1).HandleAsync("test-work", Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that TryEnqueue returns true when channel has available capacity.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges an orchestrator, acts by calling TryEnqueue, asserts true is returned
    /// indicating successful enqueue to the bounded channel.
    /// </remarks>
    [Test]
    public async Task TryEnqueue_ChannelNotFull_ReturnsTrue()
    {
        // Arrange
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);

        // Act
        var result = orchestrator.TryEnqueue("test-work");

        // Assert
        await Assert.That(result).IsTrue();
    }

    /// <summary>
    /// Verifies that TryEnqueue returns false when channel is at capacity.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges an orchestrator with blocking handler and small capacity, fills the channel,
    /// acts by attempting another enqueue, asserts false is returned when full.
    /// </remarks>
    [Test]
    public async Task TryEnqueue_ChannelFull_ReturnsFalse()
    {
        // Arrange - Use blocking handler to prevent workers from draining the channel.
        // With capacity=2 and 1 worker:
        // - work-1 is picked up by worker (removed from channel, being processed)
        // - work-2 goes into channel (pending count = 1)
        // - work-3 goes into channel (pending count = 2 = capacity)
        // - work-4 should fail since channel is full
        using var blockingHandler = new BlockingWorkHandler();
        var options = Options.Create(new WorkOrchestratorOptions { Capacity = 2, WorkerCount = 1 });
        await using var orchestrator = new WorkOrchestrator<string>(blockingHandler, options, _logger);

        // First enqueue succeeds - worker picks it up and blocks
        var firstResult = orchestrator.TryEnqueue("work-1");

        // Wait for worker to pick up work-1 (handler increments call count)
        SpinWait spinWait = default;
        while (blockingHandler.HandleCallCount == 0)
        {
            spinWait.SpinOnce();
        }

        // Second and third enqueue succeed - fill the channel to capacity
        var secondResult = orchestrator.TryEnqueue("work-2");
        var thirdResult = orchestrator.TryEnqueue("work-3");

        // Act - Fourth enqueue should fail since channel is full (2 pending = capacity)
        var fourthResult = orchestrator.TryEnqueue("work-4");

        // Assert
        await Assert.That(firstResult).IsTrue();
        await Assert.That(secondResult).IsTrue();
        await Assert.That(thirdResult).IsTrue();
        await Assert.That(fourthResult).IsFalse();

        // Cleanup - release the blocked handler so orchestrator can dispose cleanly
        blockingHandler.ReleaseAll();
    }

    /// <summary>
    /// Verifies that the worker loop processes multiple enqueued work items.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges an orchestrator with handler that counts invocations, enqueues three items,
    /// asserts all three items are processed by the worker loop.
    /// </remarks>
    [Test]
    public async Task WorkerLoop_ProcessesMultipleItems()
    {
        // Arrange
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);
        var processedCount = 0;
        var allProcessed = new TaskCompletionSource();

        _handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                if (Interlocked.Increment(ref processedCount) == 3)
                {
                    allProcessed.SetResult();
                }

                return ValueTask.CompletedTask;
            });

        // Act
        await orchestrator.EnqueueAsync("work-1").ConfigureAwait(false);
        await orchestrator.EnqueueAsync("work-2").ConfigureAwait(false);
        await orchestrator.EnqueueAsync("work-3").ConfigureAwait(false);
        await allProcessed.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        // Assert
        await Assert.That(processedCount).IsEqualTo(3);
    }

    /// <summary>
    /// Verifies that the worker loop continues processing after handler throws an exception.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges a handler that throws on first call then succeeds, enqueues two items,
    /// asserts both items are processed despite the first exception.
    /// </remarks>
    [Test]
    public async Task WorkerLoop_ContinuesAfterHandlerException()
    {
        // Arrange
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);
        var processedCount = 0;
        var allProcessed = new TaskCompletionSource();

        _handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var count = Interlocked.Increment(ref processedCount);
                if (count == 1)
                {
                    throw new InvalidOperationException("Test exception");
                }

                if (count == 2)
                {
                    allProcessed.SetResult();
                }

                return ValueTask.CompletedTask;
            });

        // Act
        await orchestrator.EnqueueAsync("work-1").ConfigureAwait(false);
        await orchestrator.EnqueueAsync("work-2").ConfigureAwait(false);
        await allProcessed.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        // Assert
        await Assert.That(processedCount).IsEqualTo(2);
    }

    /// <summary>
    /// Verifies that PendingCount reflects the current queue depth accurately.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges an orchestrator with blocking handler, enqueues items while worker is blocked,
    /// asserts PendingCount matches the number of unprocessed items in the channel.
    /// </remarks>
    [Test]
    public async Task PendingCount_ReflectsQueueDepth()
    {
        // Arrange - Use blocking handler to prevent workers from draining the channel.
        // The worker will pick up work-1 and block, leaving subsequent items pending.
        using var blockingHandler = new BlockingWorkHandler();
        var options = Options.Create(new WorkOrchestratorOptions { WorkerCount = 1 });
        await using var orchestrator = new WorkOrchestrator<string>(blockingHandler, options, _logger);

        // Enqueue first item - worker picks it up and blocks
        orchestrator.TryEnqueue("work-1");

        // Wait for worker to pick up work-1
        SpinWait spinWait = default;
        while (blockingHandler.HandleCallCount == 0)
        {
            spinWait.SpinOnce();
        }

        // Act - Enqueue more items while worker is blocked
        orchestrator.TryEnqueue("work-2");
        orchestrator.TryEnqueue("work-3");

        // Assert - 2 items pending (work-1 is being processed, work-2 and work-3 in queue)
        await Assert.That(orchestrator.PendingCount).IsEqualTo(2);

        // Cleanup - release the blocked handler so orchestrator can dispose cleanly
        blockingHandler.ReleaseAll();
    }

    /// <summary>
    /// Verifies that ActiveWorkers returns the configured worker count.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges options with 4 workers, constructs an orchestrator, asserts ActiveWorkers is 4.
    /// Worker count is determined at construction and remains constant.
    /// </remarks>
    [Test]
    public async Task ActiveWorkers_MatchesConfiguration()
    {
        // Arrange
        var options = Options.Create(new WorkOrchestratorOptions { WorkerCount = 4 });
        await using var orchestrator = new WorkOrchestrator<string>(_handler, options, _logger);

        // Assert
        await Assert.That(orchestrator.ActiveWorkers).IsEqualTo(4);
    }

    /// <summary>
    /// Verifies that Capacity returns the configured channel capacity.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges options with 256 capacity, constructs an orchestrator, asserts Capacity is 256.
    /// Capacity determines the bounded channel size.
    /// </remarks>
    [Test]
    public async Task Capacity_ReturnsConfiguredValue()
    {
        // Arrange
        var options = Options.Create(new WorkOrchestratorOptions { Capacity = 256 });
        await using var orchestrator = new WorkOrchestrator<string>(_handler, options, _logger);

        // Assert
        await Assert.That(orchestrator.Capacity).IsEqualTo(256);
    }

    /// <summary>
    /// Verifies that Writer property provides access to the channel writer.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges an orchestrator, asserts Writer property is not null.
    /// Exposes the underlying ChannelWriter for advanced enqueue scenarios.
    /// </remarks>
    [Test]
    public async Task Writer_ProvidesChannelAccess()
    {
        // Arrange
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);

        // Assert
        await Assert.That(orchestrator.Writer).IsNotNull();
    }

    /// <summary>
    /// Verifies that StopAsync completes gracefully after processing pending items.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges an orchestrator, enqueues work and waits for processing, calls StopAsync,
    /// asserts StopAsync completes before timeout indicating graceful shutdown.
    /// </remarks>
    [Test]
    public async Task StopAsync_CompletesGracefully()
    {
        // Arrange
        var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);
        var processed = new TaskCompletionSource();
        _handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                processed.TrySetResult();
                return ValueTask.CompletedTask;
            });

        await orchestrator.EnqueueAsync("work-1").ConfigureAwait(false);
        await processed.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        // Act
        var stopTask = orchestrator.StopAsync();
        var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);

        // Assert - StopAsync should complete before the delay
        await Assert.That(ReferenceEquals(completed, stopTask)).IsTrue();

        // Cleanup
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that DisposeAsync properly disposes resources and completes the channel.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges an orchestrator, disposes it, acts by calling TryEnqueue,
    /// asserts false is returned indicating the channel is completed and no longer accepts work.
    /// </remarks>
    [Test]
    public async Task DisposeAsync_DisposesResources()
    {
        // Arrange
        var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);

        // Act
        await orchestrator.DisposeAsync().ConfigureAwait(false);

        // Assert - TryEnqueue should return false after dispose
        var result = orchestrator.TryEnqueue("test");
        await Assert.That(result).IsFalse();
    }
}
