// =============================================================================
// <copyright file="IWorkOrchestratorTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Reflection;
using System.Threading.Channels;

using Bifrost.Core;

using TUnit.Core;

namespace Bifrost.Tests.Contracts;

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
    /// Verifies Run method signature.
    /// </summary>
    [Test]
    public async Task Run_HasCorrectSignature()
    {
        // Arrange
        var interfaceType = typeof(IWorkOrchestrator<>);
        var method = interfaceType.GetMethod("Run");

        // Assert
        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(void));

        var parameters = method.GetParameters();
        await Assert.That(parameters).HasCount(1);
        await Assert.That(parameters[0].Name).IsEqualTo("work");
    }

    /// <summary>
    /// Verifies TryRun method signature.
    /// </summary>
    [Test]
    public async Task TryRun_HasCorrectSignature()
    {
        // Arrange
        var interfaceType = typeof(IWorkOrchestrator<>);
        var method = interfaceType.GetMethod("TryRun");

        // Assert
        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(bool));

        var parameters = method.GetParameters();
        await Assert.That(parameters).HasCount(1);
        await Assert.That(parameters[0].Name).IsEqualTo("work");
    }

    /// <summary>
    /// Verifies CreateWorkerFunction method signature (no parameters).
    /// </summary>
    [Test]
    public async Task CreateWorkerFunction_HasCorrectSignature()
    {
        // Arrange
        var interfaceType = typeof(IWorkOrchestrator<>);
        var methods = interfaceType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "CreateWorkerFunction")
            .ToArray();
        var method = methods.FirstOrDefault(m => m.GetParameters().Length == 0);

        // Assert
        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(Func<string, CancellationToken, Task>));

        var parameters = method.GetParameters();
        await Assert.That(parameters).HasCount(0);
    }

    /// <summary>
    /// Verifies CreateWorkerFunction method signature (with callback parameter).
    /// </summary>
    [Test]
    public async Task CreateWorkerFunction_WithCallback_HasCorrectSignature()
    {
        // Arrange
        var interfaceType = typeof(IWorkOrchestrator<>);
        var methods = interfaceType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "CreateWorkerFunction")
            .ToArray();
        var method = methods.FirstOrDefault(m => m.GetParameters().Length == 1);

        // Assert
        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(Func<string, CancellationToken, Task>));

        var parameters = method.GetParameters();
        await Assert.That(parameters).HasCount(1);
        await Assert.That(parameters[0].Name).IsEqualTo("stateCallback");
        await Assert.That(parameters[0].ParameterType).IsEqualTo(typeof(Action<bool>));
    }

    /// <summary>
    /// Verifies RequestScaleUpAsync method signature.
    /// </summary>
    [Test]
    public async Task RequestScaleUpAsync_HasCorrectSignature()
    {
        // Arrange
        var interfaceType = typeof(IWorkOrchestrator<>);
        var method = interfaceType.GetMethod("RequestScaleUpAsync");

        // Assert
        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(Task));

        var parameters = method.GetParameters();
        await Assert.That(parameters).HasCount(2);
        await Assert.That(parameters[0].Name).IsEqualTo("count");
        await Assert.That(parameters[0].ParameterType).IsEqualTo(typeof(int));
        await Assert.That(parameters[1].Name).IsEqualTo("cancellationToken");
        await Assert.That(parameters[1].ParameterType).IsEqualTo(typeof(CancellationToken));
        await Assert.That(parameters[1].HasDefaultValue).IsTrue();
    }

    /// <summary>
    /// Verifies RequestScaleDownAsync method signature.
    /// </summary>
    [Test]
    public async Task RequestScaleDownAsync_HasCorrectSignature()
    {
        // Arrange
        var interfaceType = typeof(IWorkOrchestrator<>);
        var method = interfaceType.GetMethod("RequestScaleDownAsync");

        // Assert
        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(Task));

        var parameters = method.GetParameters();
        await Assert.That(parameters).HasCount(2);
        await Assert.That(parameters[0].Name).IsEqualTo("count");
        await Assert.That(parameters[0].ParameterType).IsEqualTo(typeof(int));
        await Assert.That(parameters[1].Name).IsEqualTo("cancellationToken");
        await Assert.That(parameters[1].ParameterType).IsEqualTo(typeof(CancellationToken));
        await Assert.That(parameters[1].HasDefaultValue).IsTrue();
    }

    /// <summary>
    /// Verifies GetShutdownToken method signature.
    /// </summary>
    [Test]
    public async Task GetShutdownToken_HasCorrectSignature()
    {
        // Arrange
        var interfaceType = typeof(IWorkOrchestrator<>);
        var method = interfaceType.GetMethod("GetShutdownToken");

        // Assert
        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(CancellationToken));

        var parameters = method.GetParameters();
        await Assert.That(parameters).HasCount(0);
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

    /// <summary>
    /// Verifies the interface has the expected number of declared members.
    /// This catches accidental additions or removals.
    /// </summary>
    [Test]
    public async Task Interface_HasExpectedMemberCount()
    {
        // Arrange
        var interfaceType = typeof(IWorkOrchestrator<>);
        var members = interfaceType.GetMembers(
            BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance);

        // Assert - 11 methods + 4 property getters + 4 properties = 19
        await Assert.That(members.Length).IsEqualTo(19);
    }

    /// <summary>
    /// Verifies DrainAsync method signature.
    /// </summary>
    [Test]
    public async Task DrainAsync_HasCorrectSignature()
    {
        // Arrange
        var interfaceType = typeof(IWorkOrchestrator<>);
        var method = interfaceType.GetMethod("DrainAsync");

        // Assert
        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(Task));

        var parameters = method.GetParameters();
        await Assert.That(parameters).HasCount(1);
        await Assert.That(parameters[0].Name).IsEqualTo("ct");
        await Assert.That(parameters[0].ParameterType).IsEqualTo(typeof(CancellationToken));
        await Assert.That(parameters[0].HasDefaultValue).IsTrue();
    }
}