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
        _options = Options.Create(new WorkOrchestratorOptions { WorkerCount = 0 });
        _logger = Substitute.For<ILogger<WorkOrchestrator<string>>>();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies that CreateWorkerFunction returns a valid function.
    /// </summary>
    [Test]
    public async Task CreateWorkerFunction_ReturnsValidFunction()
    {
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);

        var workerFunc = orchestrator.CreateWorkerFunction();

        await Assert.That(workerFunc).IsNotNull();
    }

    /// <summary>
    /// Verifies that CreateWorkerFunction with null callback returns a valid function.
    /// </summary>
    [Test]
    public async Task CreateWorkerFunction_NullCallback_ReturnsValidFunction()
    {
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);

        var workerFunc = orchestrator.CreateWorkerFunction(stateCallback: null);

        await Assert.That(workerFunc).IsNotNull();
    }

    /// <summary>
    /// Verifies that worker invokes callback with true when starting work.
    /// </summary>
    [Test]
    public async Task CreateWorkerFunction_CallbackInvokedWithTrueOnBusy()
    {
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);
        var stateChanges = new List<bool>();
        var workStarted = new TaskCompletionSource();
        var continueWork = new TaskCompletionSource();

        _handler.HandleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => HandleWorkWithDelayAsync(workStarted, continueWork));

        var workerFunc = orchestrator.CreateWorkerFunction(busy => stateChanges.Add(busy));
        using var cts = new CancellationTokenSource();

        var workerTask = Task.Run(() => workerFunc("TestWorker", cts.Token), cts.Token);
        orchestrator.TryEnqueue("test-work");

        await workStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await Assert.That(stateChanges.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(stateChanges[0]).IsTrue();

        continueWork.SetResult();
        await cts.CancelAsync();
        await Task.WhenAny(workerTask, Task.Delay(TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// Verifies that worker invokes callback with false when completing work.
    /// </summary>
    [Test]
    public async Task CreateWorkerFunction_CallbackInvokedWithFalseOnIdle()
    {
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

        var workerTask = Task.Run(() => workerFunc("TestWorker", cts.Token), cts.Token);
        orchestrator.TryEnqueue("test-work");

        await workCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(50);

        await Assert.That(stateChanges.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(stateChanges[0]).IsTrue();
        await Assert.That(stateChanges[1]).IsFalse();

        await cts.CancelAsync();
        await Task.WhenAny(workerTask, Task.Delay(TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// Verifies that callback is invoked with false even when handler throws.
    /// </summary>
    [Test]
    public async Task CreateWorkerFunction_CallbackInvokedWithFalseAfterException()
    {
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

        var workerTask = Task.Run(() => workerFunc("TestWorker", cts.Token), cts.Token);
        orchestrator.TryEnqueue("test-work");

        await exceptionThrown.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(50);

        await Assert.That(stateChanges.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(stateChanges[0]).IsTrue();
        await Assert.That(stateChanges[1]).IsFalse();

        await cts.CancelAsync();
        await Task.WhenAny(workerTask, Task.Delay(TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// Verifies that the parameterless overload works correctly.
    /// </summary>
    [Test]
    public async Task CreateWorkerFunction_NoCallback_ProcessesWork()
    {
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

        var workerTask = Task.Run(() => workerFunc("TestWorker", cts.Token), cts.Token);
        orchestrator.TryEnqueue("test-work");

        await workProcessed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await _handler.Received(1).HandleAsync("test-work", Arg.Any<CancellationToken>());

        await cts.CancelAsync();
        await Task.WhenAny(workerTask, Task.Delay(TimeSpan.FromSeconds(1)));
    }

    private static async ValueTask HandleWorkWithDelayAsync(TaskCompletionSource workStarted, TaskCompletionSource continueWork)
    {
        workStarted.TrySetResult();
        await continueWork.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}