// =============================================================================
// <copyright file="ISchedulerTestHarnessTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Testing;

namespace Bifrost.Tests.Scheduling.Testing;

/// <summary>
/// Contract tests for <see cref="ISchedulerTestHarness"/> (Task 35, DR-9): the
/// interface exposes the three control primitives — AdvanceAsync, FireDueJobsAsync,
/// and WaitForIdleAsync — that testing code needs to drive the tick loop
/// deterministically without real wall-clock waits.
/// </summary>
public sealed class ISchedulerTestHarnessTests
{
    /// <summary>
    /// Verifies the interface declares an <c>AdvanceAsync</c> method with the
    /// expected signature: accepts a <see cref="TimeSpan"/> and returns a
    /// <see cref="Task"/>.
    /// </summary>
    [Test]
    public async Task ISchedulerTestHarness_HasAdvanceAsyncMethod()
    {
        var method = typeof(ISchedulerTestHarness).GetMethod(
            nameof(ISchedulerTestHarness.AdvanceAsync),
            [typeof(TimeSpan)]);

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(Task));
    }

    /// <summary>
    /// Verifies the interface declares a <c>FireDueJobsAsync</c> method with the
    /// expected signature: no parameters, returns a <see cref="Task"/>.
    /// </summary>
    [Test]
    public async Task ISchedulerTestHarness_HasFireDueJobsAsyncMethod()
    {
        var method = typeof(ISchedulerTestHarness).GetMethod(
            nameof(ISchedulerTestHarness.FireDueJobsAsync),
            Type.EmptyTypes);

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(Task));
    }

    /// <summary>
    /// Verifies the interface declares a <c>WaitForIdleAsync</c> method with the
    /// expected signature: accepts a <see cref="TimeSpan"/> timeout and returns a
    /// <see cref="Task"/>.
    /// </summary>
    [Test]
    public async Task ISchedulerTestHarness_HasWaitForIdleAsyncMethod()
    {
        var method = typeof(ISchedulerTestHarness).GetMethod(
            nameof(ISchedulerTestHarness.WaitForIdleAsync),
            [typeof(TimeSpan)]);

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReturnType).IsEqualTo(typeof(Task));
    }
}
