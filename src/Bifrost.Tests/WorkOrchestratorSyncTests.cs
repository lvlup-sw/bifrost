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
        // Arrange
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);
        var tcs = new TaskCompletionSource();
        _handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                tcs.TrySetResult();
                return ValueTask.CompletedTask;
            });

        // Act
        orchestrator.Run("test-work");
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        // Assert
        await _handler.Received(1).HandleAsync("test-work", Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that Run throws InvalidOperationException when the queue is full.
    /// </summary>
    [Test]
    public async Task Run_QueueFull_ThrowsInvalidOperationException()
    {
        // Arrange - Create orchestrator with capacity of 1 and block the handler
        var options = Options.Create(new WorkOrchestratorOptions { Capacity = 1, WorkerCount = 0 });
        await using var orchestrator = new WorkOrchestrator<string>(_handler, options, _logger);

        // First enqueue should succeed
        orchestrator.Run("work-1");

        // Act & Assert - Second run should throw
        await Assert.That(() => orchestrator.Run("work-2"))
            .Throws<InvalidOperationException>();
    }

    /// <summary>
    /// Verifies that TryRun returns true when channel has capacity.
    /// </summary>
    [Test]
    public async Task TryRun_ChannelHasCapacity_ReturnsTrue()
    {
        // Arrange
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);

        // Act
        var result = orchestrator.TryRun("test-work");

        // Assert
        await Assert.That(result).IsTrue();
    }

    /// <summary>
    /// Verifies that TryRun returns false when the queue is full.
    /// </summary>
    [Test]
    public async Task TryRun_QueueFull_ReturnsFalse()
    {
        // Arrange - Create orchestrator with capacity of 1 and no workers
        var options = Options.Create(new WorkOrchestratorOptions { Capacity = 1, WorkerCount = 0 });
        await using var orchestrator = new WorkOrchestrator<string>(_handler, options, _logger);

        // First enqueue should succeed
        var firstResult = orchestrator.TryRun("work-1");

        // Act - Second should fail since channel is full
        var secondResult = orchestrator.TryRun("work-2");

        // Assert
        await Assert.That(firstResult).IsTrue();
        await Assert.That(secondResult).IsFalse();
    }

    /// <summary>
    /// Verifies that TryRun does not throw when the queue is full.
    /// </summary>
    [Test]
    public async Task TryRun_QueueFull_DoesNotThrow()
    {
        // Arrange - Create orchestrator with capacity of 1 and no workers
        var options = Options.Create(new WorkOrchestratorOptions { Capacity = 1, WorkerCount = 0 });
        await using var orchestrator = new WorkOrchestrator<string>(_handler, options, _logger);

        // Fill the queue
        orchestrator.TryRun("work-1");

        // Act & Assert - Should not throw
        var exception = default(Exception);
        try
        {
            orchestrator.TryRun("work-2");
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
        // Arrange
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);
        var tcs = new TaskCompletionSource();
        _handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                tcs.TrySetResult();
                return ValueTask.CompletedTask;
            });

        // Act
        var result = orchestrator.TryRun("test-work");
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        // Assert
        await Assert.That(result).IsTrue();
        await _handler.Received(1).HandleAsync("test-work", Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }
}