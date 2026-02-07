// =============================================================================
// <copyright file="WorkOrchestratorOptionsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.ComponentModel.DataAnnotations;
using System.Reflection;

using Bifrost.Core;

using TUnit.Core;

namespace Bifrost.Tests.Configuration;

/// <summary>
/// Tests for <see cref="WorkOrchestratorOptions"/> configuration.
/// </summary>
[Property("Category", "Unit")]
public class WorkOrchestratorOptionsTests
{
    /// <summary>
    /// Verifies that default values are reasonable.
    /// </summary>
    [Test]
    public async Task WorkOrchestratorOptions_DefaultValues_AreReasonable()
    {
        // Arrange & Act
        var options = new WorkOrchestratorOptions();

        // Assert
        await Assert.That(options.Capacity).IsEqualTo(128);
        await Assert.That(options.WorkerCount).IsEqualTo(2);
    }

    /// <summary>
    /// Verifies Capacity property has Range attribute.
    /// </summary>
    [Test]
    public async Task Capacity_HasRangeAttribute()
    {
        // Arrange
        var property = typeof(WorkOrchestratorOptions).GetProperty("Capacity");
        var attribute = property?.GetCustomAttribute<RangeAttribute>();

        // Assert
        await Assert.That(attribute).IsNotNull();
        await Assert.That(attribute!.Minimum).IsEqualTo(1);
        await Assert.That(attribute.Maximum).IsEqualTo(10000);
    }

    /// <summary>
    /// Verifies WorkerCount property has Range attribute.
    /// </summary>
    [Test]
    public async Task WorkerCount_HasRangeAttribute()
    {
        // Arrange
        var property = typeof(WorkOrchestratorOptions).GetProperty("WorkerCount");
        var attribute = property?.GetCustomAttribute<RangeAttribute>();

        // Assert
        await Assert.That(attribute).IsNotNull();
        await Assert.That(attribute!.Minimum).IsEqualTo(1);
        await Assert.That(attribute.Maximum).IsEqualTo(100);
    }

    /// <summary>
    /// Verifies properties can be set.
    /// </summary>
    [Test]
    public async Task Properties_CanBeSet()
    {
        // Arrange
        var options = new WorkOrchestratorOptions();

        // Act
        options.Capacity = 256;
        options.WorkerCount = 4;

        // Assert
        await Assert.That(options.Capacity).IsEqualTo(256);
        await Assert.That(options.WorkerCount).IsEqualTo(4);
    }

    /// <summary>
    /// Verifies the class is public.
    /// </summary>
    [Test]
    public async Task Class_IsPublic()
    {
        // Arrange
        var type = typeof(WorkOrchestratorOptions);

        // Assert
        await Assert.That(type.IsPublic).IsTrue();
        await Assert.That(type.IsClass).IsTrue();
    }
}