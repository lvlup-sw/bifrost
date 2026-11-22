// =============================================================================
// <copyright file="ControlPortTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling.Ports;

using TUnit.Core;

namespace Bifrost.Tests.Autoscaling.Ports;

/// <summary>
/// Tests for <see cref="IAutoscalingControlPort"/> interface contract.
/// </summary>
[Property("Category", "Unit")]
public class ControlPortTests
{
    /// <summary>
    /// Verifies that IAutoscalingControlPort defines RequestScaleUpAsync method.
    /// </summary>
    [Test]
    public async Task Interface_DefinesRequestScaleUpAsyncMethod()
    {
        var type = typeof(IAutoscalingControlPort);

        var method = type.GetMethod(nameof(IAutoscalingControlPort.RequestScaleUpAsync));

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(Task));

        var parameters = method.GetParameters();
        await Assert.That(parameters).HasCount().EqualTo(2);
        await Assert.That(parameters[0].ParameterType).IsEqualTo(typeof(int));
        await Assert.That(parameters[0].Name).IsEqualTo("count");
        await Assert.That(parameters[1].ParameterType).IsEqualTo(typeof(CancellationToken));
    }

    /// <summary>
    /// Verifies that IAutoscalingControlPort defines RequestScaleDownAsync method.
    /// </summary>
    [Test]
    public async Task Interface_DefinesRequestScaleDownAsyncMethod()
    {
        var type = typeof(IAutoscalingControlPort);

        var method = type.GetMethod(nameof(IAutoscalingControlPort.RequestScaleDownAsync));

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(Task));

        var parameters = method.GetParameters();
        await Assert.That(parameters).HasCount().EqualTo(2);
        await Assert.That(parameters[0].ParameterType).IsEqualTo(typeof(int));
        await Assert.That(parameters[0].Name).IsEqualTo("count");
        await Assert.That(parameters[1].ParameterType).IsEqualTo(typeof(CancellationToken));
    }

    /// <summary>
    /// Verifies that IAutoscalingControlPort defines CreateWorkerFunction method without callback.
    /// </summary>
    [Test]
    public async Task Interface_DefinesCreateWorkerFunctionMethod()
    {
        var type = typeof(IAutoscalingControlPort);

        // Act - Get the overload without parameters
        var methods = type.GetMethods()
            .Where(m => m.Name == nameof(IAutoscalingControlPort.CreateWorkerFunction))
            .ToArray();

        var method = methods.FirstOrDefault(m => m.GetParameters().Length == 0);

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(Func<string, CancellationToken, Task>));
    }

    /// <summary>
    /// Verifies that IAutoscalingControlPort defines CreateWorkerFunction method with callback.
    /// </summary>
    [Test]
    public async Task Interface_DefinesCreateWorkerFunctionMethodWithCallback()
    {
        var type = typeof(IAutoscalingControlPort);

        // Act - Get the overload with Action<bool> parameter
        var methods = type.GetMethods()
            .Where(m => m.Name == nameof(IAutoscalingControlPort.CreateWorkerFunction))
            .ToArray();

        var method = methods.FirstOrDefault(m => m.GetParameters().Length == 1);

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(Func<string, CancellationToken, Task>));

        var parameters = method.GetParameters();
        await Assert.That(parameters[0].ParameterType).IsEqualTo(typeof(Action<bool>));
    }

    /// <summary>
    /// Verifies that IAutoscalingControlPort defines GetShutdownToken method.
    /// </summary>
    [Test]
    public async Task Interface_DefinesGetShutdownTokenMethod()
    {
        var type = typeof(IAutoscalingControlPort);

        var method = type.GetMethod(nameof(IAutoscalingControlPort.GetShutdownToken));

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(CancellationToken));
        await Assert.That(method.GetParameters()).HasCount().EqualTo(0);
    }

    /// <summary>
    /// Verifies that IAutoscalingControlPort is an interface.
    /// </summary>
    [Test]
    public async Task Interface_IsInterface()
    {
        var type = typeof(IAutoscalingControlPort);

        await Assert.That(type.IsInterface).IsTrue();
    }
}