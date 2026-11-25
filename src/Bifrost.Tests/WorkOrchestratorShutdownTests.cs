// =============================================================================
// <copyright file="WorkOrchestratorShutdownTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

namespace Bifrost.Tests;

/// <summary>
/// Tests for shutdown token support in <see cref="WorkOrchestrator{TWork}"/>.
/// </summary>
public class WorkOrchestratorShutdownTests
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
    /// Verifies that GetShutdownToken returns a valid token.
    /// </summary>
    [Test]
    public async Task GetShutdownToken_ReturnsValidToken()
    {
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);

        var token = orchestrator.GetShutdownToken();

        await Assert.That(token).IsNotEqualTo(CancellationToken.None);
    }

    /// <summary>
    /// Verifies that GetShutdownToken returns a non-canceled token initially.
    /// </summary>
    [Test]
    public async Task GetShutdownToken_InitiallyNotCanceled()
    {
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);

        var token = orchestrator.GetShutdownToken();

        await Assert.That(token.IsCancellationRequested).IsFalse();
    }

    /// <summary>
    /// Verifies that shutdown token is canceled after StopAsync.
    /// </summary>
    [Test]
    public async Task GetShutdownToken_CanceledAfterStopAsync()
    {
        var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);
        var token = orchestrator.GetShutdownToken();

        await orchestrator.AbortAsync();

        await Assert.That(token.IsCancellationRequested).IsTrue();

        await orchestrator.DisposeAsync();
    }

    /// <summary>
    /// Verifies that shutdown token is canceled after DisposeAsync.
    /// </summary>
    [Test]
    public async Task GetShutdownToken_CanceledAfterDisposeAsync()
    {
        var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);
        var token = orchestrator.GetShutdownToken();

        await orchestrator.DisposeAsync();

        await Assert.That(token.IsCancellationRequested).IsTrue();
    }

    /// <summary>
    /// Verifies that GetShutdownToken returns same token on multiple calls.
    /// </summary>
    [Test]
    public async Task GetShutdownToken_ReturnsSameTokenOnMultipleCalls()
    {
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);

        var token1 = orchestrator.GetShutdownToken();
        var token2 = orchestrator.GetShutdownToken();

        await Assert.That(token1).IsEqualTo(token2);
    }

    /// <summary>
    /// Verifies that dynamic worker receives shutdown signal via token.
    /// </summary>
    [Test]
    public async Task GetShutdownToken_WorkerReceivesShutdownSignal()
    {
        var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);
        var shutdownReceived = new TaskCompletionSource<bool>();

        var token = orchestrator.GetShutdownToken();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                shutdownReceived.TrySetResult(true);
            }
        });

        await orchestrator.AbortAsync();
        var result = await shutdownReceived.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await Assert.That(result).IsTrue();

        await orchestrator.DisposeAsync();
    }
}
