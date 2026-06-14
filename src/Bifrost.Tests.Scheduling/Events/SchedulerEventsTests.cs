// =============================================================================
// <copyright file="SchedulerEventsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Core.Events;

namespace Bifrost.Tests.Scheduling.Events;

/// <summary>
/// Tests for the scheduler event types (Task 17): each is a readonly record
/// struct carrying the documented fields, with record value-equality semantics.
/// </summary>
public sealed class SchedulerEventsTests
{
    private static readonly DateTimeOffset At =
        new(2026, 6, 13, 2, 0, 0, TimeSpan.Zero);

    private static bool IsReadonlyRecordStruct<T>()
        where T : struct
    {
        var type = typeof(T);
        var isReadOnly = type.GetCustomAttributes(inherit: false)
            .Any(a => a.GetType().Name == "IsReadOnlyAttribute");
        return isReadOnly && typeof(IEquatable<T>).IsAssignableFrom(type);
    }

    /// <summary>
    /// Verifies <see cref="JobFiredEvent"/> shape, fields, and equality.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task JobFiredEvent_HasFieldsAndEquality()
    {
        await Assert.That(IsReadonlyRecordStruct<JobFiredEvent>()).IsTrue();

        var next = At.AddDays(1);
        var e = new JobFiredEvent("job", At, next);

        await Assert.That(e.JobName).IsEqualTo("job");
        await Assert.That(e.FiredAt).IsEqualTo(At);
        await Assert.That(e.NextFireAt).IsEqualTo(next);
        await Assert.That(e).IsEqualTo(new JobFiredEvent("job", At, next));
    }

    /// <summary>
    /// Verifies <see cref="JobFireFailedEvent"/> can represent a thrown dispatch
    /// exception.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task JobFireFailedEvent_RepresentsThrownException()
    {
        await Assert.That(IsReadonlyRecordStruct<JobFireFailedEvent>()).IsTrue();

        var ex = new InvalidOperationException("boom");
        var e = new JobFireFailedEvent("job", At, ex);

        await Assert.That(e.JobName).IsEqualTo("job");
        await Assert.That(e.FiredAt).IsEqualTo(At);
        await Assert.That(e.Exception).IsSameReferenceAs((Exception)ex);
        await Assert.That(e.Reason).IsNull();
    }

    /// <summary>
    /// Verifies <see cref="JobFireFailedEvent"/> can represent a non-exception
    /// admission rejection via <c>Reason</c> with a null exception.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task JobFireFailedEvent_RepresentsAdmissionRejection()
    {
        var e = new JobFireFailedEvent("job", At, Exception: null, Reason: "Rejected(CapacityExceeded)");

        await Assert.That(e.Exception).IsNull();
        await Assert.That(e.Reason).IsEqualTo("Rejected(CapacityExceeded)");
    }

    /// <summary>
    /// Verifies <see cref="JobFireFailedEvent.Reason"/> defaults to null when omitted.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task JobFireFailedEvent_ReasonDefaultsToNull()
    {
        var e = new JobFireFailedEvent("job", At, new Exception());

        await Assert.That(e.Reason).IsNull();
    }

    /// <summary>
    /// Verifies <see cref="JobMissedFireEvent"/> shape and fields.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task JobMissedFireEvent_HasFields()
    {
        await Assert.That(IsReadonlyRecordStruct<JobMissedFireEvent>()).IsTrue();

        var e = new JobMissedFireEvent("job", 3, MissedFirePolicy.Coalesce);

        await Assert.That(e.JobName).IsEqualTo("job");
        await Assert.That(e.MissedCount).IsEqualTo(3);
        await Assert.That(e.Policy).IsEqualTo(MissedFirePolicy.Coalesce);
    }

    /// <summary>
    /// Verifies the single-name lifecycle events expose <c>JobName</c>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task LifecycleEvents_ExposeJobName()
    {
        await Assert.That(IsReadonlyRecordStruct<JobRegisteredEvent>()).IsTrue();
        await Assert.That(IsReadonlyRecordStruct<JobUnregisteredEvent>()).IsTrue();
        await Assert.That(IsReadonlyRecordStruct<JobPausedEvent>()).IsTrue();
        await Assert.That(IsReadonlyRecordStruct<JobResumedEvent>()).IsTrue();

        await Assert.That(new JobRegisteredEvent("job").JobName).IsEqualTo("job");
        await Assert.That(new JobUnregisteredEvent("job").JobName).IsEqualTo("job");
        await Assert.That(new JobPausedEvent("job").JobName).IsEqualTo("job");
        await Assert.That(new JobResumedEvent("job").JobName).IsEqualTo("job");
    }

    /// <summary>
    /// Verifies <see cref="SchedulerFaultedEvent"/> shape and fields.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task SchedulerFaultedEvent_HasFields()
    {
        await Assert.That(IsReadonlyRecordStruct<SchedulerFaultedEvent>()).IsTrue();

        var ex = new InvalidOperationException("scheduler down");
        var e = new SchedulerFaultedEvent(ex, At);

        await Assert.That(e.Exception).IsSameReferenceAs((Exception)ex);
        await Assert.That(e.FaultedAt).IsEqualTo(At);
    }
}
