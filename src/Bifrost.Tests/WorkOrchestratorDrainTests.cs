// =============================================================================
// <copyright file="WorkOrchestratorDrainTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

namespace Bifrost.Tests;

/// <summary>
/// Tests for <see cref="WorkOrchestrator{TWork}.DrainAsync"/> method.
/// </summary>
[Property("Category", "Unit")]
public class WorkOrchestratorDrainTests
{
    private ILogger<WorkOrchestrator<string>> _logger = null!;

    /// <summary>
    /// Sets up test dependencies before each test.
    /// </summary>
    [Before(Test)]
    public Task Setup()
    {
        _logger = Substitute.For<ILogger<WorkOrchestrator<string>>>();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies that DrainAsync processes all remaining queued items before completing.
    /// </summary>
    [Test]
    public async Task DrainAsync_ProcessesRemainingItems()
    {
        // Arrange - use a handler that tracks processed items
        var processedItems = new List<string>();
        var handler = Substitute.For<IWorkHandler<string>>();
        handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                lock (processedItems)
                {
                    processedItems.Add(callInfo.Arg<string>());
                }

                return ValueTask.CompletedTask;
            });

        var options = Options.Create(new WorkOrchestratorOptions { Capacity = 128, WorkerCount = 1 });
        var orchestrator = new WorkOrchestrator<string>(handler, options, _logger);

        // Enqueue items
        await orchestrator.EnqueueAsync("item-1").ConfigureAwait(false);
        await orchestrator.EnqueueAsync("item-2").ConfigureAwait(false);
        await orchestrator.EnqueueAsync("item-3").ConfigureAwait(false);

        // Act - drain should process all remaining items
        await orchestrator.DrainAsync().ConfigureAwait(false);

        // Assert
        await Assert.That(processedItems.Count).IsEqualTo(3);
        await Assert.That(processedItems).Contains("item-1");
        await Assert.That(processedItems).Contains("item-2");
        await Assert.That(processedItems).Contains("item-3");

        // Cleanup
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that after DrainAsync starts, TryEnqueue returns false (rejects new work).
    /// </summary>
    [Test]
    public async Task DrainAsync_RejectsNewEnqueues()
    {
        // Arrange
        var handler = Substitute.For<IWorkHandler<string>>();
        handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);

        var options = Options.Create(new WorkOrchestratorOptions { Capacity = 128, WorkerCount = 1 });
        var orchestrator = new WorkOrchestrator<string>(handler, options, _logger);

        // Act - drain the orchestrator
        await orchestrator.DrainAsync().ConfigureAwait(false);

        // After drain, TryEnqueue should return false
        var result = orchestrator.TryEnqueue("new-work");

        // Assert
        await Assert.That(result).IsFalse();

        // Cleanup
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that DrainAsync on an empty queue completes immediately.
    /// </summary>
    [Test]
    public async Task DrainAsync_EmptyQueue_CompletesImmediately()
    {
        // Arrange
        var handler = Substitute.For<IWorkHandler<string>>();
        handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);

        var options = Options.Create(new WorkOrchestratorOptions { Capacity = 128, WorkerCount = 1 });
        var orchestrator = new WorkOrchestrator<string>(handler, options, _logger);

        // Act - drain empty orchestrator should complete quickly
        var drainTask = orchestrator.DrainAsync();
        var completed = await Task.WhenAny(drainTask, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);

        // Assert - drain should have completed (not the 5s delay)
        await Assert.That(ReferenceEquals(completed, drainTask)).IsTrue();

        // Cleanup
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that DrainAsync honors the cancellation token.
    /// </summary>
    [Test]
    public async Task DrainAsync_WithCancellation_ThrowsOperationCanceled()
    {
        // Arrange - use a blocking handler so workers never finish
        var blockStarted = new TaskCompletionSource();
        var handler = Substitute.For<IWorkHandler<string>>();
        handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                blockStarted.TrySetResult();
                // Block for a long time
                return new ValueTask(Task.Delay(TimeSpan.FromMinutes(10), callInfo.Arg<CancellationToken>()));
            });

        var options = Options.Create(new WorkOrchestratorOptions { Capacity = 128, WorkerCount = 1 });
        var orchestrator = new WorkOrchestrator<string>(handler, options, _logger);

        // Enqueue work to keep a worker busy
        await orchestrator.EnqueueAsync("blocking-work").ConfigureAwait(false);
        await blockStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        // Act & Assert - drain with already-cancelled token should throw
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync().ConfigureAwait(false);

        await Assert.That(async () => await orchestrator.DrainAsync(cts.Token).ConfigureAwait(false))
            .Throws<TaskCanceledException>();

        // Cleanup
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that PendingCount is zero after DrainAsync completes.
    /// </summary>
    [Test]
    public async Task DrainAsync_PendingCountIsZero_AfterDrain()
    {
        // Arrange
        var handler = Substitute.For<IWorkHandler<string>>();
        handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);

        var options = Options.Create(new WorkOrchestratorOptions { Capacity = 128, WorkerCount = 2 });
        var orchestrator = new WorkOrchestrator<string>(handler, options, _logger);

        // Enqueue items
        await orchestrator.EnqueueAsync("item-1").ConfigureAwait(false);
        await orchestrator.EnqueueAsync("item-2").ConfigureAwait(false);

        // Act
        await orchestrator.DrainAsync().ConfigureAwait(false);

        // Assert
        await Assert.That(orchestrator.PendingCount).IsEqualTo(0);

        // Cleanup
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }
}
