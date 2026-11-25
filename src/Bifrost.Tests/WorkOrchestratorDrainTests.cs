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

        await orchestrator.EnqueueAsync("item-1");
        await orchestrator.EnqueueAsync("item-2");
        await orchestrator.EnqueueAsync("item-3");

        await orchestrator.DrainAsync();

        await Assert.That(processedItems.Count).IsEqualTo(3);
        await Assert.That(processedItems).Contains("item-1");
        await Assert.That(processedItems).Contains("item-2");
        await Assert.That(processedItems).Contains("item-3");

        await orchestrator.DisposeAsync();
    }

    /// <summary>
    /// Verifies that after DrainAsync starts, TryEnqueue returns false (rejects new work).
    /// </summary>
    [Test]
    public async Task DrainAsync_RejectsNewEnqueues()
    {
        var handler = Substitute.For<IWorkHandler<string>>();
        handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);

        var options = Options.Create(new WorkOrchestratorOptions { Capacity = 128, WorkerCount = 1 });
        var orchestrator = new WorkOrchestrator<string>(handler, options, _logger);

        await orchestrator.DrainAsync();

        var result = orchestrator.TryEnqueue("new-work");

        await Assert.That(result).IsFalse();

        await orchestrator.DisposeAsync();
    }

    /// <summary>
    /// Verifies that DrainAsync on an empty queue completes immediately.
    /// </summary>
    [Test]
    public async Task DrainAsync_EmptyQueue_CompletesImmediately()
    {
        var handler = Substitute.For<IWorkHandler<string>>();
        handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);

        var options = Options.Create(new WorkOrchestratorOptions { Capacity = 128, WorkerCount = 1 });
        var orchestrator = new WorkOrchestrator<string>(handler, options, _logger);

        var drainTask = orchestrator.DrainAsync();
        var completed = await Task.WhenAny(drainTask, Task.Delay(TimeSpan.FromSeconds(5)));

        await Assert.That(ReferenceEquals(completed, drainTask)).IsTrue();

        await orchestrator.DisposeAsync();
    }

    /// <summary>
    /// Verifies that DrainAsync honors the cancellation token.
    /// </summary>
    [Test]
    public async Task DrainAsync_WithCancellation_ThrowsOperationCanceled()
    {
        var blockStarted = new TaskCompletionSource();
        var handler = Substitute.For<IWorkHandler<string>>();
        handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                blockStarted.TrySetResult();
                return new ValueTask(Task.Delay(TimeSpan.FromMinutes(10), callInfo.Arg<CancellationToken>()));
            });

        var options = Options.Create(new WorkOrchestratorOptions { Capacity = 128, WorkerCount = 1 });
        var orchestrator = new WorkOrchestrator<string>(handler, options, _logger);

        await orchestrator.EnqueueAsync("blocking-work");
        await blockStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.That(async () => await orchestrator.DrainAsync(cts.Token))
            .Throws<TaskCanceledException>();

        await orchestrator.DisposeAsync();
    }

    /// <summary>
    /// Verifies that PendingCount is zero after DrainAsync completes.
    /// </summary>
    [Test]
    public async Task DrainAsync_PendingCountIsZero_AfterDrain()
    {
        var handler = Substitute.For<IWorkHandler<string>>();
        handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);

        var options = Options.Create(new WorkOrchestratorOptions { Capacity = 128, WorkerCount = 2 });
        var orchestrator = new WorkOrchestrator<string>(handler, options, _logger);

        await orchestrator.EnqueueAsync("item-1");
        await orchestrator.EnqueueAsync("item-2");

        await orchestrator.DrainAsync();

        await Assert.That(orchestrator.PendingCount).IsEqualTo(0);

        await orchestrator.DisposeAsync();
    }
}
