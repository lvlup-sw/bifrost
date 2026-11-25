// =============================================================================
// <copyright file="WorkOrchestratorScalingTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

namespace Bifrost.Tests;

/// <summary>
/// Tests for scaling request methods on <see cref="WorkOrchestrator{TWork}"/>.
/// </summary>
public class WorkOrchestratorScalingTests
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
    /// Verifies that RequestScaleUpAsync completes successfully.
    /// </summary>
    [Test]
    public async Task RequestScaleUpAsync_CompletesSuccessfully()
    {
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);

        await orchestrator.RequestScaleUpAsync(2);
    }

    /// <summary>
    /// Verifies that RequestScaleDownAsync completes successfully.
    /// </summary>
    [Test]
    public async Task RequestScaleDownAsync_CompletesSuccessfully()
    {
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);

        await orchestrator.RequestScaleDownAsync(1);
    }

    /// <summary>
    /// Verifies that RequestScaleUpAsync with zero count completes successfully.
    /// </summary>
    [Test]
    public async Task RequestScaleUpAsync_ZeroCount_CompletesSuccessfully()
    {
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);

        await orchestrator.RequestScaleUpAsync(0);
    }

    /// <summary>
    /// Verifies that RequestScaleDownAsync with zero count completes successfully.
    /// </summary>
    [Test]
    public async Task RequestScaleDownAsync_ZeroCount_CompletesSuccessfully()
    {
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);

        await orchestrator.RequestScaleDownAsync(0);
    }

    /// <summary>
    /// Verifies that RequestScaleUpAsync respects cancellation token.
    /// </summary>
    [Test]
    public async Task RequestScaleUpAsync_WithCancellation_CompletesBeforeToken()
    {
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);
        using var cts = new CancellationTokenSource();

        var task = orchestrator.RequestScaleUpAsync(5, cts.Token);
        await task;

        await Assert.That(task.IsCompleted).IsTrue();
    }

    /// <summary>
    /// Verifies that RequestScaleDownAsync respects cancellation token.
    /// </summary>
    [Test]
    public async Task RequestScaleDownAsync_WithCancellation_CompletesBeforeToken()
    {
        await using var orchestrator = new WorkOrchestrator<string>(_handler, _options, _logger);
        using var cts = new CancellationTokenSource();

        var task = orchestrator.RequestScaleDownAsync(3, cts.Token);
        await task;

        await Assert.That(task.IsCompleted).IsTrue();
    }
}