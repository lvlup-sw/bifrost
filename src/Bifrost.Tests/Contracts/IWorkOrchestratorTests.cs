// =============================================================================
// <copyright file="IWorkOrchestratorTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Reflection;

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
    /// <remarks>
    /// The <c>Writer</c> escape hatch was removed in v0.5.0, so the interface must not have a
    /// <c>Writer</c> property.
    /// </remarks>
    [Test]
    public async Task IWorkOrchestrator_Interface_DefinesRequiredMembers()
    {
        var interfaceType = typeof(IWorkOrchestrator<>);

        await Assert.That(interfaceType).IsNotNull();
        await Assert.That(interfaceType.IsInterface).IsTrue();
        await Assert.That(interfaceType.IsGenericTypeDefinition).IsTrue();

        var methods = interfaceType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Concat(typeof(IWorkWriter<>).GetMethods()).ToArray();
        await Assert.That(methods.Any(m => m.Name == "EnqueueAsync")).IsTrue();
        await Assert.That(methods.Any(m => m.Name == "TryEnqueue")).IsTrue();
        await Assert.That(methods.Any(m => m.Name == "AbortAsync")).IsTrue();

        var properties = interfaceType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        await Assert.That(properties.Any(p => p.Name == "PendingCount")).IsTrue();
        await Assert.That(properties.Any(p => p.Name == "ActiveWorkers")).IsTrue();
        await Assert.That(properties.Any(p => p.Name == "Capacity")).IsTrue();

        await Assert.That(properties.Any(p => p.Name == "Writer")).IsFalse();
    }

    /// <summary>
    /// Verifies EnqueueAsync method signature.
    /// </summary>
    [Test]
    public async Task EnqueueAsync_HasCorrectSignature()
    {
        var interfaceType = typeof(IWorkWriter<>);
        var method = interfaceType.GetMethod("EnqueueAsync");

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(ValueTask<EnqueueResult>));

        var parameters = method.GetParameters();
        await Assert.That(parameters).HasCount(3);
        await Assert.That(parameters[0].Name).IsEqualTo("work");
        await Assert.That(parameters[1].Name).IsEqualTo("options");
        await Assert.That(parameters[1].ParameterType).IsEqualTo(typeof(WorkEnqueueOptions));
        await Assert.That(parameters[1].HasDefaultValue).IsTrue();
        await Assert.That(parameters[2].Name).IsEqualTo("ct");
        await Assert.That(parameters[2].ParameterType).IsEqualTo(typeof(CancellationToken));
        await Assert.That(parameters[2].HasDefaultValue).IsTrue();
    }

    /// <summary>
    /// Verifies TryEnqueue method signature.
    /// </summary>
    [Test]
    public async Task TryEnqueue_HasCorrectSignature()
    {
        var interfaceType = typeof(IWorkWriter<>);
        var method = interfaceType.GetMethod("TryEnqueue");

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(bool));

        var parameters = method.GetParameters();
        await Assert.That(parameters).HasCount(2);
        await Assert.That(parameters[0].Name).IsEqualTo("work");
        await Assert.That(parameters[1].Name).IsEqualTo("options");
        await Assert.That(parameters[1].ParameterType).IsEqualTo(typeof(WorkEnqueueOptions));
        await Assert.That(parameters[1].HasDefaultValue).IsTrue();
    }

    /// <summary>
    /// Verifies StopAsync method signature.
    /// </summary>
    [Test]
    public async Task StopAsync_HasCorrectSignature()
    {
        var interfaceType = typeof(IWorkOrchestrator<>);
        var method = interfaceType.GetMethod("AbortAsync");

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
        var interfaceType = typeof(IWorkOrchestrator<>);
        var property = interfaceType.GetProperty("PendingCount");

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
        var interfaceType = typeof(IWorkOrchestrator<>);
        var property = interfaceType.GetProperty("ActiveWorkers");

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
        var interfaceType = typeof(IWorkOrchestrator<>);
        var property = interfaceType.GetProperty("Capacity");

        await Assert.That(property).IsNotNull();
        await Assert.That(property!.PropertyType).IsEqualTo(typeof(int));
        await Assert.That(property.CanRead).IsTrue();
        await Assert.That(property.CanWrite).IsFalse();
    }

    /// <summary>
    /// Verifies the Writer escape hatch has been removed (v0.5.0 break).
    /// </summary>
    [Test]
    public async Task Writer_PropertyRemoved()
    {
        var interfaceType = typeof(IWorkOrchestrator<>);
        var property = interfaceType.GetProperty("Writer");

        await Assert.That(property).IsNull();
    }

    /// <summary>
    /// Verifies Run method signature.
    /// </summary>
    [Test]
    public async Task Run_HasCorrectSignature()
    {
        var interfaceType = typeof(IWorkWriter<>);
        var method = interfaceType.GetMethod("Enqueue");

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(EnqueueResult));

        var parameters = method.GetParameters();
        await Assert.That(parameters).HasCount(2);
        await Assert.That(parameters[0].Name).IsEqualTo("work");
        await Assert.That(parameters[1].Name).IsEqualTo("options");
        await Assert.That(parameters[1].HasDefaultValue).IsTrue();
    }

    /// <summary>
    /// Verifies TryRun method signature.
    /// </summary>
    [Test]
    public async Task TryRun_HasCorrectSignature()
    {
        var interfaceType = typeof(IWorkWriter<>);
        var method = interfaceType.GetMethod("TryEnqueue");

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(bool));

        var parameters = method.GetParameters();
        await Assert.That(parameters).HasCount(2);
        await Assert.That(parameters[0].Name).IsEqualTo("work");
        await Assert.That(parameters[1].Name).IsEqualTo("options");
        await Assert.That(parameters[1].HasDefaultValue).IsTrue();
    }

    /// <summary>
    /// Verifies CreateWorkerFunction method signature (no parameters).
    /// </summary>
    [Test]
    public async Task CreateWorkerFunction_HasCorrectSignature()
    {
        var interfaceType = typeof(IWorkOrchestrator<>);
        var methods = interfaceType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "CreateWorkerFunction")
            .ToArray();
        var method = methods.FirstOrDefault(m => m.GetParameters().Length == 0);

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
        var interfaceType = typeof(IWorkOrchestrator<>);
        var methods = interfaceType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "CreateWorkerFunction")
            .ToArray();
        var method = methods.FirstOrDefault(m => m.GetParameters().Length == 1);

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
        var interfaceType = typeof(IWorkOrchestrator<>);
        var method = interfaceType.GetMethod("RequestScaleUpAsync");

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
        var interfaceType = typeof(IWorkOrchestrator<>);
        var method = interfaceType.GetMethod("RequestScaleDownAsync");

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
        var interfaceType = typeof(IWorkOrchestrator<>);
        var method = interfaceType.GetMethod("GetShutdownToken");

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
        var interfaceType = typeof(IWorkOrchestrator<>);

        await Assert.That(typeof(IAsyncDisposable).IsAssignableFrom(interfaceType)).IsTrue();
    }

    /// <summary>
    /// Verifies DrainAsync method signature.
    /// </summary>
    [Test]
    public async Task DrainAsync_HasCorrectSignature()
    {
        var interfaceType = typeof(IWorkOrchestrator<>);
        var method = interfaceType.GetMethod("DrainAsync");

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(Task));

        var parameters = method.GetParameters();
        await Assert.That(parameters).HasCount(1);
        await Assert.That(parameters[0].Name).IsEqualTo("ct");
        await Assert.That(parameters[0].ParameterType).IsEqualTo(typeof(CancellationToken));
        await Assert.That(parameters[0].HasDefaultValue).IsTrue();
    }
}
