// =============================================================================
// <copyright file="ResiliencySettingsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Resilience;

using TUnit.Core;

namespace Bifrost.Tests.Resilience;

/// <summary>
/// Tests for <see cref="ResiliencySettings"/> configuration.
/// </summary>
[Property("Category", "Unit")]
public class ResiliencySettingsTests
{
    /// <summary>
    /// Verifies Key returns expected configuration section name.
    /// </summary>
    [Test]
    public async Task Key_ReturnsExpectedSectionName()
    {
        // Act
        var key = ResiliencySettings.Key;

        // Assert
        await Assert.That(key).IsEqualTo("ResiliencySettings");
    }

    /// <summary>
    /// Verifies RetryCount has expected default value.
    /// </summary>
    [Test]
    public async Task RetryCount_DefaultValue_IsThree()
    {
        // Arrange
        var settings = new ResiliencySettings();

        // Assert
        await Assert.That(settings.RetryCount).IsEqualTo(3);
    }

    /// <summary>
    /// Verifies RetryIntervalSeconds has expected default value.
    /// </summary>
    [Test]
    public async Task RetryIntervalSeconds_DefaultValue_IsTwo()
    {
        // Arrange
        var settings = new ResiliencySettings();

        // Assert
        await Assert.That(settings.RetryIntervalSeconds).IsEqualTo(2);
    }

    /// <summary>
    /// Verifies UseExponentialBackoff has expected default value.
    /// </summary>
    [Test]
    public async Task UseExponentialBackoff_DefaultValue_IsTrue()
    {
        // Arrange
        var settings = new ResiliencySettings();

        // Assert
        await Assert.That(settings.UseExponentialBackoff).IsTrue();
    }

    /// <summary>
    /// Verifies TimeoutIntervalSeconds has expected default value.
    /// </summary>
    [Test]
    public async Task TimeoutIntervalSeconds_DefaultValue_IsFive()
    {
        // Arrange
        var settings = new ResiliencySettings();

        // Assert
        await Assert.That(settings.TimeoutIntervalSeconds).IsEqualTo(5);
    }

    /// <summary>
    /// Verifies BulkheadMaxParallelization has expected default value.
    /// </summary>
    [Test]
    public async Task BulkheadMaxParallelization_DefaultValue_IsTen()
    {
        // Arrange
        var settings = new ResiliencySettings();

        // Assert
        await Assert.That(settings.BulkheadMaxParallelization).IsEqualTo(10);
    }

    /// <summary>
    /// Verifies BulkheadMaxQueuingActions has expected default value.
    /// </summary>
    [Test]
    public async Task BulkheadMaxQueuingActions_DefaultValue_IsTwenty()
    {
        // Arrange
        var settings = new ResiliencySettings();

        // Assert
        await Assert.That(settings.BulkheadMaxQueuingActions).IsEqualTo(20);
    }

    /// <summary>
    /// Verifies CircuitBreakerCount has expected default value.
    /// </summary>
    [Test]
    public async Task CircuitBreakerCount_DefaultValue_IsThree()
    {
        // Arrange
        var settings = new ResiliencySettings();

        // Assert
        await Assert.That(settings.CircuitBreakerCount).IsEqualTo(3);
    }

    /// <summary>
    /// Verifies CircuitBreakerIntervalMinutes has expected default value.
    /// </summary>
    [Test]
    public async Task CircuitBreakerIntervalMinutes_DefaultValue_IsOne()
    {
        // Arrange
        var settings = new ResiliencySettings();

        // Assert
        await Assert.That(settings.CircuitBreakerIntervalMinutes).IsEqualTo(1);
    }

    /// <summary>
    /// Verifies all properties can be set via init.
    /// </summary>
    [Test]
    public async Task Settings_CanBeConfiguredViaInit()
    {
        // Arrange
        var settings = new ResiliencySettings
        {
            RetryCount = 5,
            RetryIntervalSeconds = 10,
            UseExponentialBackoff = false,
            TimeoutIntervalSeconds = 30,
            BulkheadMaxParallelization = 20,
            BulkheadMaxQueuingActions = 50,
            CircuitBreakerCount = 10,
            CircuitBreakerIntervalMinutes = 5
        };

        // Assert
        await Assert.That(settings.RetryCount).IsEqualTo(5);
        await Assert.That(settings.RetryIntervalSeconds).IsEqualTo(10);
        await Assert.That(settings.UseExponentialBackoff).IsFalse();
        await Assert.That(settings.TimeoutIntervalSeconds).IsEqualTo(30);
        await Assert.That(settings.BulkheadMaxParallelization).IsEqualTo(20);
        await Assert.That(settings.BulkheadMaxQueuingActions).IsEqualTo(50);
        await Assert.That(settings.CircuitBreakerCount).IsEqualTo(10);
        await Assert.That(settings.CircuitBreakerIntervalMinutes).IsEqualTo(5);
    }
}