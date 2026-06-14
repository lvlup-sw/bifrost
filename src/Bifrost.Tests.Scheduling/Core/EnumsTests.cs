// =============================================================================
// <copyright file="EnumsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Scheduling.Core;

namespace Bifrost.Tests.Scheduling.Core;

/// <summary>
/// Shape tests for the scheduling contract enums <see cref="JobState"/> and
/// <see cref="MissedFirePolicy"/> (Task 5).
/// </summary>
public sealed class EnumsTests
{
    /// <summary>
    /// Verifies <see cref="JobState"/> declares the expected lifecycle members.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task JobState_HasExpectedMembers()
    {
        await Assert.That(Enum.IsDefined(JobState.Running)).IsTrue();
        await Assert.That(Enum.IsDefined(JobState.Paused)).IsTrue();
        await Assert.That(Enum.IsDefined(JobState.Faulted)).IsTrue();
        await Assert.That(Enum.IsDefined(JobState.Completed)).IsTrue();

        var names = Enum.GetNames<JobState>();
        await Assert.That(names).HasCount(4);
    }

    /// <summary>
    /// Verifies <see cref="MissedFirePolicy"/> declares the expected catch-up members.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task MissedFirePolicy_HasExpectedMembers()
    {
        await Assert.That(Enum.IsDefined(MissedFirePolicy.Coalesce)).IsTrue();
        await Assert.That(Enum.IsDefined(MissedFirePolicy.FireAllMissed)).IsTrue();
        await Assert.That(Enum.IsDefined(MissedFirePolicy.SkipMissed)).IsTrue();

        var names = Enum.GetNames<MissedFirePolicy>();
        await Assert.That(names).HasCount(3);
    }

    /// <summary>
    /// Verifies that <see cref="MissedFirePolicy.Coalesce"/> is the default
    /// (numeric value 0), so an unset policy field coalesces missed fires.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task MissedFirePolicy_CoalesceIsDefault()
    {
        // Materialize the default into locals so the analyzer does not see a
        // compile-time constant being asserted (TUnitAssertions0005).
        var unset = default(MissedFirePolicy);
        var coalesceNumericValue = (int)MissedFirePolicy.Coalesce;

        await Assert.That(unset).IsEqualTo(MissedFirePolicy.Coalesce);
        await Assert.That(coalesceNumericValue).IsEqualTo(0);
    }
}
