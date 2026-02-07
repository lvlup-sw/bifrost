// =============================================================================
// <copyright file="WorkOrchestratorStateTrackingTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

namespace Bifrost.Tests;

/// <summary>
/// Tests for worker state tracking callback support in <see cref="WorkOrchestrator{TWork}"/>.
/// </summary>
public class WorkOrchestratorStateTrackingTests
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
        _options = Options.Create(new WorkOrchestratorOptions { WorkerCount = 0 }); // No default workers
        _logger = Substitute.For<ILogger<WorkOrchestrator<string>>>();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies that CreateWorkerFunction returns a valid function.
    /// </summary>
    [Test]
    public async Task CreateWorkerFunction_ReturnsValidFunction()
    {
        // Arrange
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);

        // Act
        var workerFunc = orchestrator.CreateWorkerFunction();

        // Assert
        await Assert.That(workerFunc).IsNotNull();
    }

    /// <summary>
    /// Verifies that CreateWorkerFunction with null callback returns a valid function.
    /// </summary>
    [Test]
    public async Task CreateWorkerFunction_NullCallback_ReturnsValidFunction()
    {
        // Arrange
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);

        // Act
        var workerFunc = orchestrator.CreateWorkerFunction(stateCallback: null);

        // Assert
        await Assert.That(workerFunc).IsNotNull();
    }

    /// <summary>
    /// Verifies that worker invokes callback with true when starting work.
    /// </summary>
    [Test]
    public async Task CreateWorkerFunction_CallbackInvokedWithTrueOnBusy()
    {
        // Arrange
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);
        var stateChanges = new List<bool>();
        var workStarted = new TaskCompletionSource();
        var continueWork = new TaskCompletionSource();

        _handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => HandleWorkWithDelayAsync(workStarted, continueWork));

        var workerFunc = orchestrator.CreateWorkerFunction(busy => stateChanges.Add(busy));
        using var cts = new CancellationTokenSource();

        // Act - Start worker and enqueue work
        var workerTask = Task.Run(() => workerFunc("TestWorker", cts.Token), cts.Token);
        orchestrator.TryEnqueue("test-work");

        await workStarted.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        // Assert - Should have received busy=true
        await Assert.That(stateChanges.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(stateChanges[0]).IsTrue();

        // Cleanup
        continueWork.SetResult();
        await cts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAny(workerTask, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that worker invokes callback with false when completing work.
    /// </summary>
    [Test]
    public async Task CreateWorkerFunction_CallbackInvokedWithFalseOnIdle()
    {
        // Arrange
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);
        var stateChanges = new List<bool>();
        var workCompleted = new TaskCompletionSource();

        _handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workCompleted.TrySetResult();
                return ValueTask.CompletedTask;
            });

        var workerFunc = orchestrator.CreateWorkerFunction(busy => stateChanges.Add(busy));
        using var cts = new CancellationTokenSource();

        // Act - Start worker and enqueue work
        var workerTask = Task.Run(() => workerFunc("TestWorker", cts.Token), cts.Token);
        orchestrator.TryEnqueue("test-work");

        await workCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        await Task.Delay(50).ConfigureAwait(false); // Give callback time to complete

        // Assert - Should have received busy=true followed by busy=false
        await Assert.That(stateChanges.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(stateChanges[0]).IsTrue();
        await Assert.That(stateChanges[1]).IsFalse();

        // Cleanup
        await cts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAny(workerTask, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that callback is invoked with false even when handler throws.
    /// </summary>
    [Test]
    public async Task CreateWorkerFunction_CallbackInvokedWithFalseAfterException()
    {
        // Arrange
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);
        var stateChanges = new List<bool>();
        var exceptionThrown = new TaskCompletionSource();

        _handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                exceptionThrown.TrySetResult();
                throw new InvalidOperationException("Test exception");
            });

        var workerFunc = orchestrator.CreateWorkerFunction(busy => stateChanges.Add(busy));
        using var cts = new CancellationTokenSource();

        // Act - Start worker and enqueue work
        var workerTask = Task.Run(() => workerFunc("TestWorker", cts.Token), cts.Token);
        orchestrator.TryEnqueue("test-work");

        await exceptionThrown.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        await Task.Delay(50).ConfigureAwait(false); // Give callback time to complete

        // Assert - Should still receive busy=true followed by busy=false
        await Assert.That(stateChanges.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(stateChanges[0]).IsTrue();
        await Assert.That(stateChanges[1]).IsFalse();

        // Cleanup
        await cts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAny(workerTask, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that the parameterless overload works correctly.
    /// </summary>
    [Test]
    public async Task CreateWorkerFunction_NoCallback_ProcessesWork()
    {
        // Arrange
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);
        var workProcessed = new TaskCompletionSource();

        _handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workProcessed.TrySetResult();
                return ValueTask.CompletedTask;
            });

        var workerFunc = orchestrator.CreateWorkerFunction();
        using var cts = new CancellationTokenSource();

        // Act - Start worker and enqueue work
        var workerTask = Task.Run(() => workerFunc("TestWorker", cts.Token), cts.Token);
        orchestrator.TryEnqueue("test-work");

        await workProcessed.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        // Assert
        await _handler.Received(1).HandleAsync("test-work", Arg.Any<CancellationToken>()).ConfigureAwait(false);

        // Cleanup
        await cts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAny(workerTask, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
    }

    private static async ValueTask HandleWorkWithDelayAsync(TaskCompletionSource workStarted, TaskCompletionSource continueWork)
    {
        workStarted.TrySetResult();
        await continueWork.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }
}