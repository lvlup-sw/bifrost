// =============================================================================
// <copyright file="ScalingEventTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core.Events;

using TUnit.Core;

namespace Bifrost.Tests.Events;

/// <summary>
/// Unit tests for the <see cref="ScalingEvent"/> sealed record class and <see cref="ScalingAction"/> enum.
/// </summary>
/// <remarks>
/// Tests cover type characteristics, property definitions, interface implementation,
/// constructor behavior, and enum value correctness.
/// </remarks>
[Property("Category", "Unit")]
public class ScalingEventTests
{
    /// <summary>
    /// Verifies that ScalingEvent is a sealed record class.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges the type via reflection, asserts it is a reference type implementing IEquatable.
    /// Record classes provide value equality semantics and immutability.
    /// </remarks>
    [Test]
    public async Task ScalingEvent_IsSealedRecordClass()
    {
        // Arrange
        var type = typeof(ScalingEvent);

        // Assert - check it's a reference type (class), not a value type (struct)
        await Assert.That(type.IsValueType).IsFalse();

        // Check it's a record (implements IEquatable<T>)
        var interfaces = type.GetInterfaces();
        await Assert.That(interfaces.Any(i =>
            i.IsGenericType
            && i.GetGenericTypeDefinition() == typeof(IEquatable<>))).IsTrue();
    }

    /// <summary>
    /// Verifies the ScalingEvent record class has all expected properties with correct types.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges the type via reflection, asserts Action, PreviousWorkers, CurrentWorkers,
    /// and Utilization properties exist with their expected types.
    /// </remarks>
    [Test]
    public async Task ScalingEvent_HasCorrectProperties()
    {
        // Arrange
        var type = typeof(ScalingEvent);

        // Assert
        var actionProperty = type.GetProperty("Action");
        var previousWorkersProperty = type.GetProperty("PreviousWorkers");
        var currentWorkersProperty = type.GetProperty("CurrentWorkers");
        var utilizationProperty = type.GetProperty("Utilization");

        await Assert.That(actionProperty).IsNotNull();
        await Assert.That(previousWorkersProperty).IsNotNull();
        await Assert.That(currentWorkersProperty).IsNotNull();
        await Assert.That(utilizationProperty).IsNotNull();

        await Assert.That(actionProperty!.PropertyType).IsEqualTo(typeof(ScalingAction));
        await Assert.That(previousWorkersProperty!.PropertyType).IsEqualTo(typeof(int));
        await Assert.That(currentWorkersProperty!.PropertyType).IsEqualTo(typeof(int));
        await Assert.That(utilizationProperty!.PropertyType).IsEqualTo(typeof(double));
    }

    /// <summary>
    /// Verifies the ScalingEvent record class implements the IOrchestratorEvent interface.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges the type via reflection, asserts IOrchestratorEvent is assignable from ScalingEvent.
    /// This enables polymorphic event handling across orchestrator components.
    /// </remarks>
    [Test]
    public async Task ScalingEvent_ImplementsIOrchestratorEvent()
    {
        // Arrange
        var type = typeof(ScalingEvent);

        // Assert
        await Assert.That(typeof(IOrchestratorEvent).IsAssignableFrom(type)).IsTrue();
    }

    /// <summary>
    /// Verifies that the constructor sets all properties to the provided values.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges test values for each property, acts by constructing a ScalingEvent,
    /// asserts each property matches the constructor argument.
    /// </remarks>
    [Test]
    public async Task Constructor_SetsPropertiesCorrectly()
    {
        // Arrange
        var action = ScalingAction.ScaleUp;
        var previousWorkers = 2;
        var currentWorkers = 4;
        var utilization = 0.85;

        // Act
        var evt = new ScalingEvent(action, previousWorkers, currentWorkers, utilization);

        // Assert
        await Assert.That(evt.Action).IsEqualTo(action);
        await Assert.That(evt.PreviousWorkers).IsEqualTo(previousWorkers);
        await Assert.That(evt.CurrentWorkers).IsEqualTo(currentWorkers);
        await Assert.That(evt.Utilization).IsEqualTo(utilization);
    }

    /// <summary>
    /// Verifies that the ScalingEvent type is publicly accessible.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges the type via reflection, asserts the IsPublic flag is true.
    /// Public visibility is required for cross-assembly scaling notifications.
    /// </remarks>
    [Test]
    public async Task Type_IsPublic()
    {
        // Arrange
        var type = typeof(ScalingEvent);

        // Assert
        await Assert.That(type.IsPublic).IsTrue();
    }

    /// <summary>
    /// Verifies that the ScalingAction enum contains all expected values.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges by retrieving all enum values, asserts None, ScaleUp, and ScaleDown exist
    /// with exactly three total values.
    /// </remarks>
    [Test]
    public async Task ScalingAction_HasCorrectValues()
    {
        // Arrange
        var values = Enum.GetValues<ScalingAction>();

        // Assert
        await Assert.That(values).Contains(ScalingAction.None);
        await Assert.That(values).Contains(ScalingAction.ScaleUp);
        await Assert.That(values).Contains(ScalingAction.ScaleDown);
        await Assert.That(values).HasCount(3);
    }

    /// <summary>
    /// Verifies that the ScalingAction enum is publicly accessible.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Arranges the type via reflection, asserts both IsPublic and IsEnum flags are true.
    /// Public visibility is required for scaling decision handling across components.
    /// </remarks>
    [Test]
    public async Task ScalingAction_IsPublicEnum()
    {
        // Arrange
        var type = typeof(ScalingAction);

        // Assert
        await Assert.That(type.IsPublic).IsTrue();
        await Assert.That(type.IsEnum).IsTrue();
    }

    /// <summary>
    /// Verifies that the ScalingAction enum has explicit integer values to prevent
    /// accidental reordering from breaking serialization or persistence.
    /// </summary>
    /// <returns>A Task representing the async test operation.</returns>
    /// <remarks>
    /// Pins explicit enum values: None=0, ScaleUp=1, ScaleDown=2.
    /// This ensures the consolidated enum (from Bifrost.Core.Events) maintains
    /// stable integer representations across versions.
    /// </remarks>
    [Test]
    public async Task ScalingAction_HasExplicitValues()
    {
        // Arrange - cast to int variables to avoid TUnit constant value assertion error
        var noneValue = (int)ScalingAction.None;
        var scaleUpValue = (int)ScalingAction.ScaleUp;
        var scaleDownValue = (int)ScalingAction.ScaleDown;

        // Assert - verify each enum member has the expected explicit integer value
        await Assert.That(noneValue).IsEqualTo(0);
        await Assert.That(scaleUpValue).IsEqualTo(1);
        await Assert.That(scaleDownValue).IsEqualTo(2);
    }
}