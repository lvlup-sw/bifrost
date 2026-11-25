// =============================================================================
// <copyright file="ScheduleRegistryRegisterTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Registry;

using Microsoft.Extensions.Time.Testing;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Bifrost.Tests.Scheduling.Registry;

/// <summary>
/// Tests for <see cref="ScheduleRegistry"/> registration and unregistration
/// (Task 19, DR-1): name validation, duplicate detection, store persistence on
/// the happy path, removal, and save-failure rollback.
/// </summary>
public sealed class ScheduleRegistryRegisterTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Verifies registering a new job adds it to the registry and persists it.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Register_NewJob_AddsToRegistryAndPersists()
    {
        var store = Substitute.For<IScheduleStore>();
        var registry = new ScheduleRegistry(store, new FakeTimeProvider(Now));
        var cadence = new IntervalCadence(TimeSpan.FromHours(1));

        await registry.RegisterAsync("daily-report", cadence, MissedFirePolicy.Coalesce, new StubDispatcher());

        var job = registry.GetJob("daily-report");
        await Assert.That(job).IsNotNull();
        await Assert.That(job!.Value.Name).IsEqualTo("daily-report");
        await store.Received(1).SaveAsync(
            Arg.Is<JobRecord>(j => j.Name == "daily-report"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies registering a duplicate name throws <see cref="DuplicateJobNameException"/>.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Register_DuplicateName_Throws()
    {
        var store = Substitute.For<IScheduleStore>();
        var registry = new ScheduleRegistry(store, new FakeTimeProvider(Now));
        var cadence = new IntervalCadence(TimeSpan.FromHours(1));
        await registry.RegisterAsync("daily-report", cadence, MissedFirePolicy.Coalesce, new StubDispatcher());

        await Assert.That(async () =>
                await registry.RegisterAsync("daily-report", cadence, MissedFirePolicy.Coalesce, new StubDispatcher()))
            .Throws<DuplicateJobNameException>();
    }

    /// <summary>
    /// Verifies an empty name is rejected.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Register_EmptyName_Throws()
        => await AssertInvalidName(string.Empty);

    /// <summary>
    /// Verifies an uppercase name is rejected (names are lowercase).
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Register_UppercaseName_Throws()
        => await AssertInvalidName("DailyReport");

    /// <summary>
    /// Verifies a name longer than 128 characters is rejected.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Register_TooLongName_Throws()
        => await AssertInvalidName(new string('a', 129));

    /// <summary>
    /// Verifies a name with a leading hyphen is rejected.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Register_LeadingHyphenName_Throws()
        => await AssertInvalidName("-daily");

    /// <summary>
    /// Verifies unregistering an existing job removes it and deletes it from the store.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Unregister_Existing_RemovesAndDeletes()
    {
        var store = Substitute.For<IScheduleStore>();
        var registry = new ScheduleRegistry(store, new FakeTimeProvider(Now));
        await registry.RegisterAsync(
                "daily-report", new IntervalCadence(TimeSpan.FromHours(1)), MissedFirePolicy.Coalesce, new StubDispatcher());

        var removed = await registry.UnregisterAsync("daily-report");

        await Assert.That(removed).IsTrue();
        await Assert.That(registry.GetJob("daily-report")).IsNull();
        await store.Received(1).DeleteAsync("daily-report", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies unregistering a non-existent job returns false.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Unregister_NonExistent_ReturnsFalse()
    {
        var store = Substitute.For<IScheduleStore>();
        var registry = new ScheduleRegistry(store, new FakeTimeProvider(Now));

        var removed = await registry.UnregisterAsync("missing");

        await Assert.That(removed).IsFalse();
    }

    /// <summary>
    /// Verifies a store save failure rolls back the registration: the job is not
    /// left in the registry and the exception propagates.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task Register_WhenSaveThrows_RollsBackAndPropagates()
    {
        var store = Substitute.For<IScheduleStore>();
        store.SaveAsync(Arg.Any<JobRecord>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("store down"));
        var registry = new ScheduleRegistry(store, new FakeTimeProvider(Now));

        await Assert.That(async () =>
                await registry.RegisterAsync(
                        "daily-report", new IntervalCadence(TimeSpan.FromHours(1)), MissedFirePolicy.Coalesce, new StubDispatcher()))
            .Throws<InvalidOperationException>();

        await Assert.That(registry.GetJob("daily-report")).IsNull();
    }

    private static async Task AssertInvalidName(string name)
    {
        var store = Substitute.For<IScheduleStore>();
        var registry = new ScheduleRegistry(store, new FakeTimeProvider(Now));

        await Assert.That(async () =>
                await registry.RegisterAsync(
                        name, new IntervalCadence(TimeSpan.FromHours(1)), MissedFirePolicy.Coalesce, new StubDispatcher()))
            .Throws<ArgumentException>();
    }

    private sealed class StubDispatcher : IJobDispatcher
    {
        public ValueTask DispatchAsync(JobFireContext context, CancellationToken ct) => ValueTask.CompletedTask;
    }
}
