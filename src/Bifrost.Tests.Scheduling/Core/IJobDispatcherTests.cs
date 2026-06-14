// =============================================================================
// <copyright file="IJobDispatcherTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Reflection;

using Bifrost.Scheduling.Core;

namespace Bifrost.Tests.Scheduling.Core;

/// <summary>
/// Shape tests for the dispatch port <see cref="IJobDispatcher"/> and its
/// <see cref="JobFireContext"/> payload (Task 16).
/// </summary>
public sealed class IJobDispatcherTests
{
    /// <summary>
    /// A minimal <see cref="IServiceProvider"/> double — the contract tests only
    /// need a non-null reference to carry through <see cref="JobFireContext"/>,
    /// not a working DI container.
    /// </summary>
    private sealed class StubServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>
    /// Verifies <see cref="IJobDispatcher"/> is a public interface exposing
    /// <c>DispatchAsync(JobFireContext, CancellationToken)</c> returning
    /// <see cref="ValueTask"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task IJobDispatcher_HasDispatchAsync()
    {
        var type = typeof(IJobDispatcher);

        await Assert.That(type.IsInterface).IsTrue();
        await Assert.That(type.IsPublic).IsTrue();

        var method = type.GetMethod(
            "DispatchAsync",
            BindingFlags.Public | BindingFlags.Instance);

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(ValueTask));

        var parameters = method.GetParameters();
        await Assert.That(parameters).HasCount(2);
        await Assert.That(parameters[0].ParameterType).IsEqualTo(typeof(JobFireContext));
        await Assert.That(parameters[0].Name).IsEqualTo("context");
        await Assert.That(parameters[1].ParameterType).IsEqualTo(typeof(CancellationToken));
        await Assert.That(parameters[1].Name).IsEqualTo("ct");
    }

    /// <summary>
    /// Verifies <see cref="JobFireContext"/> is a readonly record struct.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task JobFireContext_IsReadonlyRecordStruct()
    {
        var type = typeof(JobFireContext);

        await Assert.That(type.IsValueType).IsTrue();

        // The C# compiler marks readonly structs with [IsReadOnlyAttribute].
        var isReadOnly = type.GetCustomAttributes()
            .Any(a => a.GetType().Name == "IsReadOnlyAttribute");
        await Assert.That(isReadOnly).IsTrue();

        // record structs synthesize an IEquatable<T> implementation.
        await Assert.That(typeof(IEquatable<JobFireContext>).IsAssignableFrom(type)).IsTrue();
    }

    /// <summary>
    /// Verifies positional construction sets every field.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Construction_SetsAllFields()
    {
        var fireTime = new DateTimeOffset(2026, 6, 13, 2, 0, 0, TimeSpan.Zero);
        var nextFireAt = new DateTimeOffset(2026, 6, 14, 2, 0, 0, TimeSpan.Zero);
        IServiceProvider services = new StubServiceProvider();

        var context = new JobFireContext(
            JobName: "nightly-report",
            FireTime: fireTime,
            NextFireAt: nextFireAt,
            Services: services);

        await Assert.That(context.JobName).IsEqualTo("nightly-report");
        await Assert.That(context.FireTime).IsEqualTo(fireTime);
        await Assert.That(context.NextFireAt).IsEqualTo(nextFireAt);
        await Assert.That(context.Services).IsSameReferenceAs(services);
    }

    /// <summary>
    /// Verifies record-struct value equality for the fire context.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ValueEquality_OfEqualContexts()
    {
        var fireTime = new DateTimeOffset(2026, 6, 13, 2, 0, 0, TimeSpan.Zero);
        IServiceProvider services = new StubServiceProvider();

        var a = new JobFireContext("job", fireTime, null, services);
        var b = new JobFireContext("job", fireTime, null, services);

        await Assert.That(a).IsEqualTo(b);
    }
}
