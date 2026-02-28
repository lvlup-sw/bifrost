// =============================================================================
// <copyright file="DeadLetterQueueHealthCheckTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core.DeadLetter;
using Bifrost.DependencyInjection;
using Bifrost.HealthChecks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using NSubstitute;

using TUnit.Core;

namespace Bifrost.Tests.HealthChecks;

/// <summary>
/// Tests for <see cref="DeadLetterQueueHealthCheck{TWork}"/>.
/// </summary>
[Property("Category", "Unit")]
public class DeadLetterQueueHealthCheckTests
{
    /// <summary>
    /// Verifies that the constructor throws when DLQ is null.
    /// </summary>
    [Test]
    public async Task Constructor_ThrowsWhenDlqNull()
    {
        await Assert.That(() => new DeadLetterQueueHealthCheck<string>(null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that an empty DLQ returns Healthy.
    /// </summary>
    [Test]
    public async Task CheckHealthAsync_EmptyDlq_ReturnsHealthy()
    {
        // Arrange
        var dlq = Substitute.For<IDeadLetterQueue<string>>();
        dlq.Count.Returns(0);
        var healthCheck = new DeadLetterQueueHealthCheck<string>(dlq);

        // Act
        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(), CancellationToken.None).ConfigureAwait(false);

        // Assert
        await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
    }

    /// <summary>
    /// Verifies that below degraded threshold returns Healthy.
    /// </summary>
    [Test]
    public async Task CheckHealthAsync_BelowDegraded_ReturnsHealthy()
    {
        // Arrange
        var dlq = Substitute.For<IDeadLetterQueue<string>>();
        dlq.Count.Returns(50);
        var healthCheck = new DeadLetterQueueHealthCheck<string>(dlq);

        // Act
        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(), CancellationToken.None).ConfigureAwait(false);

        // Assert
        await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
    }

    /// <summary>
    /// Verifies that at degraded threshold returns Degraded.
    /// </summary>
    [Test]
    public async Task CheckHealthAsync_AtDegradedThreshold_ReturnsDegraded()
    {
        // Arrange
        var dlq = Substitute.For<IDeadLetterQueue<string>>();
        dlq.Count.Returns(100);
        var healthCheck = new DeadLetterQueueHealthCheck<string>(dlq);

        // Act
        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(), CancellationToken.None).ConfigureAwait(false);

        // Assert
        await Assert.That(result.Status).IsEqualTo(HealthStatus.Degraded);
    }

    /// <summary>
    /// Verifies that at unhealthy threshold returns Unhealthy.
    /// </summary>
    [Test]
    public async Task CheckHealthAsync_AtUnhealthyThreshold_ReturnsUnhealthy()
    {
        // Arrange
        var dlq = Substitute.For<IDeadLetterQueue<string>>();
        dlq.Count.Returns(1000);
        var healthCheck = new DeadLetterQueueHealthCheck<string>(dlq);

        // Act
        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(), CancellationToken.None).ConfigureAwait(false);

        // Assert
        await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
    }

    /// <summary>
    /// Verifies that health check includes a descriptive message.
    /// </summary>
    [Test]
    public async Task CheckHealthAsync_IncludesDescriptiveMessage()
    {
        // Arrange
        var dlq = Substitute.For<IDeadLetterQueue<string>>();
        dlq.Count.Returns(50);
        var healthCheck = new DeadLetterQueueHealthCheck<string>(dlq);

        // Act
        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(), CancellationToken.None).ConfigureAwait(false);

        // Assert
        await Assert.That(result.Description).IsNotNull();
        await Assert.That(result.Description!.Length).IsGreaterThan(0);
    }

    /// <summary>
    /// Verifies that health check is registered via builder extension.
    /// </summary>
    [Test]
    public async Task HealthCheckRegistration_RegisteredViaBuilder()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<Core.IWorkHandler<string>>());
        var dlq = Substitute.For<IDeadLetterQueue<string>>();
        dlq.Count.Returns(0);
        services.AddSingleton(dlq);

        services.AddHealthChecks()
            .AddDeadLetterQueueHealthCheck<string>();

        var provider = services.BuildServiceProvider();

        // Act
        var healthCheckService = provider.GetService<HealthCheckService>();

        // Assert
        await Assert.That(healthCheckService).IsNotNull();
    }
}
