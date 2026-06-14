// =============================================================================
// <copyright file="ISchedulerEventSinkTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Reflection;

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Core.Events;

namespace Bifrost.Tests.Scheduling.Core;

/// <summary>
/// Shape tests for the <see cref="ISchedulerEventSink"/> publication seam: the
/// minimal forward-compatible surface dispatchers (and later the tick loop and
/// registry) publish scheduler events through. The concrete fan-out sink lands
/// in a later group; this only pins the contract.
/// </summary>
public sealed class ISchedulerEventSinkTests
{
    /// <summary>
    /// Verifies <see cref="ISchedulerEventSink"/> is a public interface exposing a
    /// generic <c>Publish&lt;TEvent&gt;(in TEvent)</c> returning <see langword="void"/>,
    /// constrained to value types so callers publish without boxing.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ISchedulerEventSink_HasGenericPublish()
    {
        var type = typeof(ISchedulerEventSink);

        await Assert.That(type.IsInterface).IsTrue();
        await Assert.That(type.IsPublic).IsTrue();

        var method = type.GetMethod(
            "Publish",
            BindingFlags.Public | BindingFlags.Instance);

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.IsGenericMethodDefinition).IsTrue();
        await Assert.That(method.ReturnType).IsEqualTo(typeof(void));

        var generics = method.GetGenericArguments();
        await Assert.That(generics).HasCount(1);

        // The TEvent parameter is constrained to a value type (struct).
        var constraints = generics[0].GenericParameterAttributes;
        await Assert.That(constraints.HasFlag(
            GenericParameterAttributes.NotNullableValueTypeConstraint)).IsTrue();

        var parameters = method.GetParameters();
        await Assert.That(parameters).HasCount(1);
        await Assert.That(parameters[0].ParameterType.IsByRef).IsTrue();
        await Assert.That(parameters[0].IsIn).IsTrue();
    }

    /// <summary>
    /// Verifies a struct event can be published through the seam — exercising the
    /// recording fake used across the dispatch tests.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Publish_RecordsEvent_OnRecordingSink()
    {
        var sink = new RecordingSchedulerEventSink();
        var at = new DateTimeOffset(2026, 6, 13, 2, 0, 0, TimeSpan.Zero);

        sink.Publish(new JobFiredEvent("job", at, null));

        await Assert.That(sink.Published).HasCount(1);
        await Assert.That(sink.Single<JobFiredEvent>().JobName).IsEqualTo("job");
    }
}
