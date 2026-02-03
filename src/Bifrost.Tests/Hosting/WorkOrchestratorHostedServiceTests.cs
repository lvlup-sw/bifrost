// =============================================================================
// <copyright file="WorkOrchestratorHostedServiceTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Bifrost.Tests.Hosting;

/// <summary>
/// Tests for <see cref="WorkOrchestratorHostedService{TWork}"/>.
/// </summary>
public class WorkOrchestratorHostedServiceTests
{
    /// <summary>
    /// Verifies that StartAsync completes successfully.
    /// </summary>
    [Test]
    public async Task WorkOrchestratorHostedService_StartAsync_Completes()
    {
        // Arrange
        var orchestrator = Substitute.For<IWorkOrchestrator<string>>();
        var logger = Substitute.For<ILogger<WorkOrchestratorHostedService<string>>>();
        var service = new WorkOrchestratorHostedService<string>(orchestrator, logger);

        // Act
        var task = service.StartAsync(CancellationToken.None);
        await task.ConfigureAwait(false);

        // Assert - Service starts without calling orchestrator methods (workers start on construction)
        await Assert.That(task.IsCompletedSuccessfully).IsTrue();
    }

    /// <summary>
    /// Verifies that StopAsync calls StopAsync on the orchestrator.
    /// </summary>
    [Test]
    public async Task WorkOrchestratorHostedService_StopAsync_StopsOrchestrator()
    {
        // Arrange
        var orchestrator = Substitute.For<IWorkOrchestrator<string>>();
        var logger = Substitute.For<ILogger<WorkOrchestratorHostedService<string>>>();
        var service = new WorkOrchestratorHostedService<string>(orchestrator, logger);

        // Act
        await service.StopAsync(CancellationToken.None).ConfigureAwait(false);

        // Assert
        await orchestrator.Received(1).StopAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that the constructor throws when orchestrator is null.
    /// </summary>
    [Test]
    public async Task WorkOrchestratorHostedService_Constructor_ThrowsWhenOrchestratorNull()
    {
        // Arrange
        var logger = Substitute.For<ILogger<WorkOrchestratorHostedService<string>>>();

        // Act & Assert
        await Assert.That(() => new WorkOrchestratorHostedService<string>(null!, logger))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that the constructor throws when logger is null.
    /// </summary>
    [Test]
    public async Task WorkOrchestratorHostedService_Constructor_ThrowsWhenLoggerNull()
    {
        // Arrange
        var orchestrator = Substitute.For<IWorkOrchestrator<string>>();

        // Act & Assert
        await Assert.That(() => new WorkOrchestratorHostedService<string>(orchestrator, null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that StopAsync passes the cancellation token to the orchestrator.
    /// </summary>
    [Test]
    public async Task WorkOrchestratorHostedService_StopAsync_PassesCancellationToken()
    {
        // Arrange
        var orchestrator = Substitute.For<IWorkOrchestrator<string>>();
        var logger = Substitute.For<ILogger<WorkOrchestratorHostedService<string>>>();
        var service = new WorkOrchestratorHostedService<string>(orchestrator, logger);
        using var cts = new CancellationTokenSource();

        // Act
        await service.StopAsync(cts.Token).ConfigureAwait(false);

        // Assert - Verify the token was passed
        await orchestrator.Received(1).StopAsync(cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that StartAsync can be called multiple times safely.
    /// </summary>
    [Test]
    public async Task WorkOrchestratorHostedService_StartAsync_CanBeCalledMultipleTimes()
    {
        // Arrange
        var orchestrator = Substitute.For<IWorkOrchestrator<string>>();
        var logger = Substitute.For<ILogger<WorkOrchestratorHostedService<string>>>();
        var service = new WorkOrchestratorHostedService<string>(orchestrator, logger);

        // Act - Call StartAsync twice
        var task1 = service.StartAsync(CancellationToken.None);
        await task1.ConfigureAwait(false);
        var task2 = service.StartAsync(CancellationToken.None);
        await task2.ConfigureAwait(false);

        // Assert - Both should complete successfully
        await Assert.That(task1.IsCompletedSuccessfully).IsTrue();
        await Assert.That(task2.IsCompletedSuccessfully).IsTrue();
    }
}
