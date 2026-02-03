// =============================================================================
// <copyright file="IWorkOrchestratorTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Reflection;
using System.Threading.Channels;
using Levelup.Channels.Core;
using TUnit.Core;

namespace Levelup.Channels.Tests.Contracts;

/// <summary>
/// Tests for <see cref="IWorkOrchestrator{TWork}"/> interface definition.
/// </summary>
[Property("Category", "Unit")]
public class IWorkOrchestratorTests
{
    /// <summary>
    /// Verifies that the interface defines all required members.
    /// </summary>
    [Test]
    public async Task IWorkOrchestrator_Interface_DefinesRequiredMembers()
    {
        // Arrange
        var interfaceType = typeof(IWorkOrchestrator<>);

        // Assert - interface exists and is generic
        await Assert.That(interfaceType).IsNotNull();
        await Assert.That(interfaceType.IsInterface).IsTrue();
        await Assert.That(interfaceType.IsGenericTypeDefinition).IsTrue();

        // Check for required methods
        var methods = interfaceType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        await Assert.That(methods.Any(m => m.Name == "EnqueueAsync")).IsTrue();
        await Assert.That(methods.Any(m => m.Name == "TryEnqueue")).IsTrue();
        await Assert.That(methods.Any(m => m.Name == "StopAsync")).IsTrue();

        // Check for required properties
        var properties = interfaceType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        await Assert.That(properties.Any(p => p.Name == "PendingCount")).IsTrue();
        await Assert.That(properties.Any(p => p.Name == "ActiveWorkers")).IsTrue();
        await Assert.That(properties.Any(p => p.Name == "Capacity")).IsTrue();
        await Assert.That(properties.Any(p => p.Name == "Writer")).IsTrue();
    }

    /// <summary>
    /// Verifies EnqueueAsync method signature.
    /// </summary>
    [Test]
    public async Task EnqueueAsync_HasCorrectSignature()
    {
        // Arrange
        var interfaceType = typeof(IWorkOrchestrator<>);
        var method = interfaceType.GetMethod("EnqueueAsync");

        // Assert
        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(ValueTask));

        var parameters = method.GetParameters();
        await Assert.That(parameters).HasCount(2);
        await Assert.That(parameters[0].Name).IsEqualTo("work");
        await Assert.That(parameters[1].Name).IsEqualTo("ct");
        await Assert.That(parameters[1].ParameterType).IsEqualTo(typeof(CancellationToken));
        await Assert.That(parameters[1].HasDefaultValue).IsTrue();
    }

    /// <summary>
    /// Verifies TryEnqueue method signature.
    /// </summary>
    [Test]
    public async Task TryEnqueue_HasCorrectSignature()
    {
        // Arrange
        var interfaceType = typeof(IWorkOrchestrator<>);
        var method = interfaceType.GetMethod("TryEnqueue");

        // Assert
        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(bool));

        var parameters = method.GetParameters();
        await Assert.That(parameters).HasCount(1);
        await Assert.That(parameters[0].Name).IsEqualTo("work");
    }

    /// <summary>
    /// Verifies StopAsync method signature.
    /// </summary>
    [Test]
    public async Task StopAsync_HasCorrectSignature()
    {
        // Arrange
        var interfaceType = typeof(IWorkOrchestrator<>);
        var method = interfaceType.GetMethod("StopAsync");

        // Assert
        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(Task));

        var parameters = method.GetParameters();
        await Assert.That(parameters).HasCount(1);
        await Assert.That(parameters[0].Name).IsEqualTo("ct");
        await Assert.That(parameters[0].ParameterType).IsEqualTo(typeof(CancellationToken));
        await Assert.That(parameters[0].HasDefaultValue).IsTrue();
    }

    /// <summary>
    /// Verifies PendingCount property signature.
    /// </summary>
    [Test]
    public async Task PendingCount_HasCorrectSignature()
    {
        // Arrange
        var interfaceType = typeof(IWorkOrchestrator<>);
        var property = interfaceType.GetProperty("PendingCount");

        // Assert
        await Assert.That(property).IsNotNull();
        await Assert.That(property!.PropertyType).IsEqualTo(typeof(int));
        await Assert.That(property.CanRead).IsTrue();
        await Assert.That(property.CanWrite).IsFalse();
    }

    /// <summary>
    /// Verifies ActiveWorkers property signature.
    /// </summary>
    [Test]
    public async Task ActiveWorkers_HasCorrectSignature()
    {
        // Arrange
        var interfaceType = typeof(IWorkOrchestrator<>);
        var property = interfaceType.GetProperty("ActiveWorkers");

        // Assert
        await Assert.That(property).IsNotNull();
        await Assert.That(property!.PropertyType).IsEqualTo(typeof(int));
        await Assert.That(property.CanRead).IsTrue();
        await Assert.That(property.CanWrite).IsFalse();
    }

    /// <summary>
    /// Verifies Capacity property signature.
    /// </summary>
    [Test]
    public async Task Capacity_HasCorrectSignature()
    {
        // Arrange
        var interfaceType = typeof(IWorkOrchestrator<>);
        var property = interfaceType.GetProperty("Capacity");

        // Assert
        await Assert.That(property).IsNotNull();
        await Assert.That(property!.PropertyType).IsEqualTo(typeof(int));
        await Assert.That(property.CanRead).IsTrue();
        await Assert.That(property.CanWrite).IsFalse();
    }

    /// <summary>
    /// Verifies Writer property signature.
    /// </summary>
    [Test]
    public async Task Writer_HasCorrectSignature()
    {
        // Arrange
        var interfaceType = typeof(IWorkOrchestrator<>);
        var property = interfaceType.GetProperty("Writer");

        // Assert
        await Assert.That(property).IsNotNull();
        await Assert.That(property!.PropertyType.IsGenericType).IsTrue();
        await Assert.That(property.PropertyType.GetGenericTypeDefinition()).IsEqualTo(typeof(ChannelWriter<>));
        await Assert.That(property.CanRead).IsTrue();
        await Assert.That(property.CanWrite).IsFalse();
    }

    /// <summary>
    /// Verifies interface implements IAsyncDisposable.
    /// </summary>
    [Test]
    public async Task Interface_ImplementsIAsyncDisposable()
    {
        // Arrange
        var interfaceType = typeof(IWorkOrchestrator<>);

        // Assert
        await Assert.That(typeof(IAsyncDisposable).IsAssignableFrom(interfaceType)).IsTrue();
    }
}
