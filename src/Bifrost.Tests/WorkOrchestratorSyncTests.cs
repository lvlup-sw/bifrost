// =============================================================================
// <copyright file="WorkOrchestratorSyncTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

namespace Bifrost.Tests;

/// <summary>
/// Tests for synchronous Run/TryRun methods on <see cref="WorkOrchestrator{TWork}"/>.
/// </summary>
public class WorkOrchestratorSyncTests
{
    private IWorkHandler<string> _handler = null!;
    private IOptions<WorkOrchestratorOptions> _options = null!;
    private ILogger<WorkOrchestrator<string>> _logger = null!;

    /// <summary>
    /// Sets up test dependencies before each test.
    /// </summary>
    [Before(Test)]
    public Task Setup()
    {
        _handler = Substitute.For<IWorkHandler<string>>();
        _options = Options.Create(new WorkOrchestratorOptions());
        _logger = Substitute.For<ILogger<WorkOrchestrator<string>>>();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies that Run successfully enqueues work when channel has capacity.
    /// </summary>
    [Test]
    public async Task Run_ChannelHasCapacity_EnqueuesWork()
    {
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);
        var tcs = new TaskCompletionSource();
        _handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                tcs.TrySetResult();
                return ValueTask.CompletedTask;
            });

        orchestrator.Enqueue("test-work");
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        await _handler.Received(1).HandleAsync("test-work", Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that Run throws InvalidOperationException when the queue is full.
    /// </summary>
    [Test]
    public async Task Enqueue_QueueFull_ReturnsCapacityRejection()
    {
        var options = Options.Create(new WorkOrchestratorOptions { Capacity = 1, WorkerCount = 0 });
        await using var orchestrator = new WorkOrchestrator<string>(_handler, options, _logger);

        orchestrator.Enqueue("work-1");

        await Assert.That(orchestrator.Enqueue("work-2").Reason).IsEqualTo(RejectionReason.CapacityExceeded);
    }

    /// <summary>
    /// Verifies that TryRun returns true when channel has capacity.
    /// </summary>
    [Test]
    public async Task TryRun_ChannelHasCapacity_ReturnsTrue()
    {
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);

        var result = orchestrator.TryEnqueue("test-work");

        await Assert.That(result).IsTrue();
    }

    /// <summary>
    /// Verifies that TryRun returns false when the queue is full.
    /// </summary>
    [Test]
    public async Task TryRun_QueueFull_ReturnsFalse()
    {
        var options = Options.Create(new WorkOrchestratorOptions { Capacity = 1, WorkerCount = 0 });
        await using var orchestrator = new WorkOrchestrator<string>(_handler, options, _logger);

        var firstResult = orchestrator.TryEnqueue("work-1");

        var secondResult = orchestrator.TryEnqueue("work-2");

        await Assert.That(firstResult).IsTrue();
        await Assert.That(secondResult).IsFalse();
    }

    /// <summary>
    /// Verifies that TryRun does not throw when the queue is full.
    /// </summary>
    [Test]
    public async Task TryRun_QueueFull_DoesNotThrow()
    {
        var options = Options.Create(new WorkOrchestratorOptions { Capacity = 1, WorkerCount = 0 });
        await using var orchestrator = new WorkOrchestrator<string>(_handler, options, _logger);

        orchestrator.TryEnqueue("work-1");

        var exception = default(Exception);
        try
        {
            orchestrator.TryEnqueue("work-2");
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNull();
    }

    /// <summary>
    /// Verifies that TryRun enqueues work that gets processed.
    /// </summary>
    [Test]
    public async Task TryRun_Success_WorkGetsProcessed()
    {
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);
        var tcs = new TaskCompletionSource();
        _handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                tcs.TrySetResult();
                return ValueTask.CompletedTask;
            });

        var result = orchestrator.TryEnqueue("test-work");
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        await Assert.That(result).IsTrue();
        await _handler.Received(1).HandleAsync("test-work", Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }
}
