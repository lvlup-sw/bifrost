// =============================================================================
// <copyright file="IScheduleRegistryTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Reflection;

using Bifrost.Scheduling.Core;

namespace Bifrost.Tests.Scheduling.Core;

/// <summary>
/// Shape tests for the runtime control surface (Task 15): the
/// <see cref="IScheduleRegistry"/> interface, the <see cref="JobDescriptor"/>
/// read model, and <see cref="DuplicateJobNameException"/>.
/// </summary>
public sealed class IScheduleRegistryTests
{
    /// <summary>
    /// Verifies <see cref="IScheduleRegistry"/> is a public interface.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task IScheduleRegistry_IsPublicInterface()
    {
        var type = typeof(IScheduleRegistry);

        await Assert.That(type.IsInterface).IsTrue();
        await Assert.That(type.IsPublic).IsTrue();
    }

    /// <summary>
    /// Verifies the registry exposes the expected control methods.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task IScheduleRegistry_ExposesControlMethods()
    {
        var type = typeof(IScheduleRegistry);

        foreach (var name in new[]
        {
            "RegisterAsync", "UnregisterAsync", "PauseAsync",
            "ResumeAsync", "TriggerAsync", "GetJobs", "GetJob",
        })
        {
            await Assert.That(type.GetMethod(name)).IsNotNull();
        }
    }

    /// <summary>
    /// Verifies the <c>RegisterAsync</c> signature, including the dispatcher
    /// parameter that ties registration to the dispatch port.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task RegisterAsync_HasExpectedSignature()
    {
        var method = typeof(IScheduleRegistry).GetMethod("RegisterAsync");

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(ValueTask));

        var p = method.GetParameters();
        await Assert.That(p).HasCount(5);
        await Assert.That(p[0].ParameterType).IsEqualTo(typeof(string));
        await Assert.That(p[0].Name).IsEqualTo("name");
        await Assert.That(p[1].ParameterType).IsEqualTo(typeof(Cadence));
        await Assert.That(p[2].ParameterType).IsEqualTo(typeof(MissedFirePolicy));
        await Assert.That(p[3].ParameterType).IsEqualTo(typeof(IJobDispatcher));
        await Assert.That(p[4].ParameterType).IsEqualTo(typeof(CancellationToken));
        await Assert.That(p[4].HasDefaultValue).IsTrue();
    }

    /// <summary>
    /// Verifies <c>UnregisterAsync</c> returns <see cref="ValueTask{Boolean}"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task UnregisterAsync_ReturnsValueTaskOfBool()
    {
        var method = typeof(IScheduleRegistry).GetMethod("UnregisterAsync");

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(ValueTask<bool>));
    }

    /// <summary>
    /// Verifies <c>GetJobs</c> returns <see cref="IReadOnlyList{JobDescriptor}"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task GetJobs_ReturnsReadOnlyListOfDescriptors()
    {
        var method = typeof(IScheduleRegistry).GetMethod("GetJobs");

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType)
            .IsEqualTo(typeof(IReadOnlyList<JobDescriptor>));
        await Assert.That(method.GetParameters()).HasCount(0);
    }

    /// <summary>
    /// Verifies <c>GetJob</c> takes a name and returns a nullable descriptor.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task GetJob_ReturnsNullableDescriptor()
    {
        var method = typeof(IScheduleRegistry).GetMethod("GetJob");

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(JobDescriptor?));

        var p = method.GetParameters();
        await Assert.That(p).HasCount(1);
        await Assert.That(p[0].ParameterType).IsEqualTo(typeof(string));
        await Assert.That(p[0].Name).IsEqualTo("name");
    }

    /// <summary>
    /// Verifies <see cref="JobDescriptor"/> is a readonly record struct whose
    /// positional construction sets every field.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task JobDescriptor_ConstructionSetsAllFields()
    {
        var type = typeof(JobDescriptor);
        await Assert.That(type.IsValueType).IsTrue();
        var isReadOnly = type.GetCustomAttributes()
            .Any(a => a.GetType().Name == "IsReadOnlyAttribute");
        await Assert.That(isReadOnly).IsTrue();

        var cadence = new FakeCadence();
        var lastFired = new DateTimeOffset(2026, 6, 13, 2, 0, 0, TimeSpan.Zero);
        var nextFire = new DateTimeOffset(2026, 6, 14, 2, 0, 0, TimeSpan.Zero);

        var descriptor = new JobDescriptor(
            Name: "nightly-report",
            State: JobState.Running,
            Cadence: cadence,
            LastFiredAt: lastFired,
            NextFireAt: nextFire,
            IsRunning: true);

        await Assert.That(descriptor.Name).IsEqualTo("nightly-report");
        await Assert.That(descriptor.State).IsEqualTo(JobState.Running);
        await Assert.That(descriptor.Cadence).IsEqualTo((Cadence)cadence);
        await Assert.That(descriptor.LastFiredAt).IsEqualTo(lastFired);
        await Assert.That(descriptor.NextFireAt).IsEqualTo(nextFire);
        await Assert.That(descriptor.IsRunning).IsTrue();
    }

    /// <summary>
    /// Verifies <see cref="DuplicateJobNameException"/> derives from
    /// <see cref="Exception"/>, is sealed, and exposes the offending job name.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task DuplicateJobNameException_ExposesJobName()
    {
        var type = typeof(DuplicateJobNameException);
        await Assert.That(typeof(Exception).IsAssignableFrom(type)).IsTrue();
        await Assert.That(type.IsSealed).IsTrue();

        var ex = new DuplicateJobNameException("nightly-report");

        await Assert.That(ex.JobName).IsEqualTo("nightly-report");
        await Assert.That(ex).IsAssignableTo<Exception>();
    }

    /// <summary>
    /// A trivially subclassable <see cref="Cadence"/> double for descriptor tests.
    /// </summary>
    private sealed record FakeCadence : Cadence
    {
        /// <inheritdoc/>
        public override DateTimeOffset? ComputeNextFire(DateTimeOffset? lastFiredAt, DateTimeOffset now)
            => null;
    }
}
