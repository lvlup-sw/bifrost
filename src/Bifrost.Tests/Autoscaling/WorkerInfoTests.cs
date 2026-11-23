// =============================================================================
// <copyright file="WorkerInfoTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Autoscaling;

using TUnit.Core;

namespace Bifrost.Tests.Autoscaling;

/// <summary>
/// Tests for <see cref="WorkerInfo"/> stop request atomicity.
/// </summary>
[Property("Category", "Unit")]
public class WorkerInfoTests
{
    /// <summary>
    /// Verifies that RequestStop sets StopRequested to true atomically.
    /// </summary>
    [Test]
    public async Task RequestStop_SetsFlagAtomically()
    {
        var worker = new WorkerInfo("worker-1");
        await Assert.That(worker.StopRequested).IsFalse();

        worker.RequestStop();

        await Assert.That(worker.StopRequested).IsTrue();
    }

    /// <summary>
    /// Verifies that calling RequestStop multiple times is idempotent and does not throw.
    /// </summary>
    /// <remarks>
    /// The test calls <c>RequestStop</c> two times. The calls must not throw.
    /// </remarks>
    [Test]
    public async Task RequestStop_IsIdempotent()
    {
        var worker = new WorkerInfo("worker-1");

        worker.RequestStop();
        worker.RequestStop();

        await Assert.That(worker.StopRequested).IsTrue();
    }
}
