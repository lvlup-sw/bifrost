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
        _options = Options.Create(new WorkOrchestratorOptions { WorkerCount = 0 }); // No default workers
        _logger = Substitute.For<ILogger<WorkOrchestrator<string>>>();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies that GetShutdownToken returns a valid token.
    /// </summary>
    [Test]
    public async Task GetShutdownToken_ReturnsValidToken()
    {
        // Arrange
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);

        // Act
        var token = orchestrator.GetShutdownToken();

        // Assert
        await Assert.That(token).IsNotEqualTo(CancellationToken.None);
    }

    /// <summary>
    /// Verifies that GetShutdownToken returns a non-canceled token initially.
    /// </summary>
    [Test]
    public async Task GetShutdownToken_InitiallyNotCanceled()
    {
        // Arrange
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);

        // Act
        var token = orchestrator.GetShutdownToken();

        // Assert
        await Assert.That(token.IsCancellationRequested).IsFalse();
    }

    /// <summary>
    /// Verifies that shutdown token is canceled after StopAsync.
    /// </summary>
    [Test]
    public async Task GetShutdownToken_CanceledAfterStopAsync()
    {
        // Arrange
        var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);
        var token = orchestrator.GetShutdownToken();

        // Act
        await orchestrator.StopAsync().ConfigureAwait(false);

        // Assert
        await Assert.That(token.IsCancellationRequested).IsTrue();

        // Cleanup
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that shutdown token is canceled after DisposeAsync.
    /// </summary>
    [Test]
    public async Task GetShutdownToken_CanceledAfterDisposeAsync()
    {
        // Arrange
        var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);
        var token = orchestrator.GetShutdownToken();

        // Act
        await orchestrator.DisposeAsync().ConfigureAwait(false);

        // Assert
        await Assert.That(token.IsCancellationRequested).IsTrue();
    }

    /// <summary>
    /// Verifies that GetShutdownToken returns same token on multiple calls.
    /// </summary>
    [Test]
    public async Task GetShutdownToken_ReturnsSameTokenOnMultipleCalls()
    {
        // Arrange
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);

        // Act
        var token1 = orchestrator.GetShutdownToken();
        var token2 = orchestrator.GetShutdownToken();

        // Assert
        await Assert.That(token1).IsEqualTo(token2);
    }

    /// <summary>
    /// Verifies that dynamic worker receives shutdown signal via token.
    /// </summary>
    [Test]
    public async Task GetShutdownToken_WorkerReceivesShutdownSignal()
    {
        // Arrange
        var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);
        var shutdownReceived = new TaskCompletionSource<bool>();

        var token = orchestrator.GetShutdownToken();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                shutdownReceived.TrySetResult(true);
            }
        });

        // Act
        await orchestrator.StopAsync().ConfigureAwait(false);
        var result = await shutdownReceived.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        // Assert
        await Assert.That(result).IsTrue();

        // Cleanup
        await orchestrator.DisposeAsync().ConfigureAwait(false);
    }
}
