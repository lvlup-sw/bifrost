// =============================================================================
// <copyright file="AutoscalingOptionsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Levelup.Channels.Autoscaling;
using TUnit.Core;

namespace Levelup.Channels.Tests.Autoscaling;

/// <summary>
/// Tests for <see cref="AutoscalingOptions"/> configuration.
/// </summary>
[Property("Category", "Unit")]
public class AutoscalingOptionsTests
{
    /// <summary>
    /// Verifies that default values match the design specification.
    /// </summary>
    [Test]
    public async Task AutoscalingOptions_DefaultValues_MatchDesign()
    {
        // Arrange & Act
        var options = new AutoscalingOptions();

        // Assert
        await Assert.That(options.Enabled).IsTrue();
        await Assert.That(options.MinWorkers).IsEqualTo(1);
        await Assert.That(options.MaxWorkers).IsEqualTo(16);
        await Assert.That(options.HighWatermark).IsEqualTo(0.8);
        await Assert.That(options.LowWatermark).IsEqualTo(0.3);
        await Assert.That(options.CooldownPeriod).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert.That(options.ScaleUpStep).IsEqualTo(2);
        await Assert.That(options.ScaleDownStep).IsEqualTo(1);
    }

    /// <summary>
    /// Verifies Enabled property can be set and defaults to true.
    /// </summary>
    [Test]
    public async Task Enabled_DefaultsToTrue_CanBeSet()
    {
        // Arrange
        var options = new AutoscalingOptions();

        // Assert default
        await Assert.That(options.Enabled).IsTrue();

        // Act
        options.Enabled = false;

        // Assert
        await Assert.That(options.Enabled).IsFalse();
    }

    /// <summary>
    /// Verifies MinWorkers property has Range attribute with correct bounds.
    /// </summary>
    [Test]
    public async Task MinWorkers_HasRangeAttribute()
    {
        // Arrange
        var property = typeof(AutoscalingOptions).GetProperty("MinWorkers");
        var attribute = property?.GetCustomAttribute<RangeAttribute>();

        // Assert
        await Assert.That(attribute).IsNotNull();
        await Assert.That(attribute!.Minimum).IsEqualTo(1);
        await Assert.That(attribute.Maximum).IsEqualTo(100);
    }

    /// <summary>
    /// Verifies MaxWorkers property has Range attribute with correct bounds.
    /// </summary>
    [Test]
    public async Task MaxWorkers_HasRangeAttribute()
    {
        // Arrange
        var property = typeof(AutoscalingOptions).GetProperty("MaxWorkers");
        var attribute = property?.GetCustomAttribute<RangeAttribute>();

        // Assert
        await Assert.That(attribute).IsNotNull();
        await Assert.That(attribute!.Minimum).IsEqualTo(1);
        await Assert.That(attribute.Maximum).IsEqualTo(100);
    }

    /// <summary>
    /// Verifies HighWatermark property has Range attribute with correct bounds.
    /// </summary>
    [Test]
    public async Task HighWatermark_HasRangeAttribute()
    {
        // Arrange
        var property = typeof(AutoscalingOptions).GetProperty("HighWatermark");
        var attribute = property?.GetCustomAttribute<RangeAttribute>();

        // Assert
        await Assert.That(attribute).IsNotNull();
        await Assert.That(attribute!.Minimum).IsEqualTo(0.0);
        await Assert.That(attribute.Maximum).IsEqualTo(1.0);
    }

    /// <summary>
    /// Verifies LowWatermark property has Range attribute with correct bounds.
    /// </summary>
    [Test]
    public async Task LowWatermark_HasRangeAttribute()
    {
        // Arrange
        var property = typeof(AutoscalingOptions).GetProperty("LowWatermark");
        var attribute = property?.GetCustomAttribute<RangeAttribute>();

        // Assert
        await Assert.That(attribute).IsNotNull();
        await Assert.That(attribute!.Minimum).IsEqualTo(0.0);
        await Assert.That(attribute.Maximum).IsEqualTo(1.0);
    }

    /// <summary>
    /// Verifies ScaleUpStep property has Range attribute with correct bounds.
    /// </summary>
    [Test]
    public async Task ScaleUpStep_HasRangeAttribute()
    {
        // Arrange
        var property = typeof(AutoscalingOptions).GetProperty("ScaleUpStep");
        var attribute = property?.GetCustomAttribute<RangeAttribute>();

        // Assert
        await Assert.That(attribute).IsNotNull();
        await Assert.That(attribute!.Minimum).IsEqualTo(1);
        await Assert.That(attribute.Maximum).IsEqualTo(10);
    }

    /// <summary>
    /// Verifies ScaleDownStep property has Range attribute with correct bounds.
    /// </summary>
    [Test]
    public async Task ScaleDownStep_HasRangeAttribute()
    {
        // Arrange
        var property = typeof(AutoscalingOptions).GetProperty("ScaleDownStep");
        var attribute = property?.GetCustomAttribute<RangeAttribute>();

        // Assert
        await Assert.That(attribute).IsNotNull();
        await Assert.That(attribute!.Minimum).IsEqualTo(1);
        await Assert.That(attribute.Maximum).IsEqualTo(10);
    }

    /// <summary>
    /// Verifies properties can be set.
    /// </summary>
    [Test]
    public async Task Properties_CanBeSet()
    {
        // Arrange
        var options = new AutoscalingOptions();

        // Act
        options.MinWorkers = 2;
        options.MaxWorkers = 32;
        options.HighWatermark = 0.9;
        options.LowWatermark = 0.2;
        options.CooldownPeriod = TimeSpan.FromMinutes(1);
        options.ScaleUpStep = 4;
        options.ScaleDownStep = 2;

        // Assert
        await Assert.That(options.MinWorkers).IsEqualTo(2);
        await Assert.That(options.MaxWorkers).IsEqualTo(32);
        await Assert.That(options.HighWatermark).IsEqualTo(0.9);
        await Assert.That(options.LowWatermark).IsEqualTo(0.2);
        await Assert.That(options.CooldownPeriod).IsEqualTo(TimeSpan.FromMinutes(1));
        await Assert.That(options.ScaleUpStep).IsEqualTo(4);
        await Assert.That(options.ScaleDownStep).IsEqualTo(2);
    }

    /// <summary>
    /// Verifies the class is public.
    /// </summary>
    [Test]
    public async Task Class_IsPublic()
    {
        // Arrange
        var type = typeof(AutoscalingOptions);

        // Assert
        await Assert.That(type.IsPublic).IsTrue();
        await Assert.That(type.IsClass).IsTrue();
    }
}
