// =============================================================================
// <copyright file="IScheduleStoreTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Reflection;

using Bifrost.Scheduling.Core;

namespace Bifrost.Tests.Scheduling.Core;

/// <summary>
/// Shape tests for the <see cref="IScheduleStore"/> persistence port (Task 7):
/// the type is a public interface exposing the durable-store method signatures.
/// </summary>
public sealed class IScheduleStoreTests
{
    /// <summary>
    /// Verifies <see cref="IScheduleStore"/> is a public interface.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task IScheduleStore_IsPublicInterface()
    {
        var type = typeof(IScheduleStore);

        await Assert.That(type.IsInterface).IsTrue();
        await Assert.That(type.IsPublic).IsTrue();
    }

    /// <summary>
    /// Verifies the <c>LoadAllAsync</c> signature.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task LoadAllAsync_HasExpectedSignature()
    {
        var method = typeof(IScheduleStore).GetMethod(
            "LoadAllAsync",
            BindingFlags.Public | BindingFlags.Instance);

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType)
            .IsEqualTo(typeof(ValueTask<IReadOnlyList<JobRecord>>));

        var parameters = method.GetParameters();
        await Assert.That(parameters).HasCount(1);
        await Assert.That(parameters[0].ParameterType).IsEqualTo(typeof(CancellationToken));
        await Assert.That(parameters[0].Name).IsEqualTo("ct");
    }

    /// <summary>
    /// Verifies the <c>SaveAsync</c> signature.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task SaveAsync_HasExpectedSignature()
    {
        var method = typeof(IScheduleStore).GetMethod(
            "SaveAsync",
            BindingFlags.Public | BindingFlags.Instance);

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(ValueTask));

        var parameters = method.GetParameters();
        await Assert.That(parameters).HasCount(2);
        await Assert.That(parameters[0].ParameterType).IsEqualTo(typeof(JobRecord));
        await Assert.That(parameters[0].Name).IsEqualTo("job");
        await Assert.That(parameters[1].ParameterType).IsEqualTo(typeof(CancellationToken));
        await Assert.That(parameters[1].Name).IsEqualTo("ct");
    }

    /// <summary>
    /// Verifies the <c>RecordFiredAsync</c> signature.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task RecordFiredAsync_HasExpectedSignature()
    {
        var method = typeof(IScheduleStore).GetMethod(
            "RecordFiredAsync",
            BindingFlags.Public | BindingFlags.Instance);

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(ValueTask));

        var parameters = method.GetParameters();
        await Assert.That(parameters).HasCount(4);
        await Assert.That(parameters[0].ParameterType).IsEqualTo(typeof(string));
        await Assert.That(parameters[0].Name).IsEqualTo("jobName");
        await Assert.That(parameters[1].ParameterType).IsEqualTo(typeof(DateTimeOffset));
        await Assert.That(parameters[1].Name).IsEqualTo("firedAt");
        await Assert.That(parameters[2].ParameterType).IsEqualTo(typeof(DateTimeOffset?));
        await Assert.That(parameters[2].Name).IsEqualTo("nextFireAt");
        await Assert.That(parameters[3].ParameterType).IsEqualTo(typeof(CancellationToken));
        await Assert.That(parameters[3].Name).IsEqualTo("ct");
    }

    /// <summary>
    /// Verifies the <c>DeleteAsync</c> signature.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DeleteAsync_HasExpectedSignature()
    {
        var method = typeof(IScheduleStore).GetMethod(
            "DeleteAsync",
            BindingFlags.Public | BindingFlags.Instance);

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(ValueTask));

        var parameters = method.GetParameters();
        await Assert.That(parameters).HasCount(2);
        await Assert.That(parameters[0].ParameterType).IsEqualTo(typeof(string));
        await Assert.That(parameters[0].Name).IsEqualTo("jobName");
        await Assert.That(parameters[1].ParameterType).IsEqualTo(typeof(CancellationToken));
        await Assert.That(parameters[1].Name).IsEqualTo("ct");
    }
}
