// =============================================================================
// <copyright file="WorkClassTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

using TUnit.Core;

namespace Bifrost.Tests.Core;

/// <summary>
/// Tests for the <see cref="WorkClass"/>, <see cref="EnqueueResult"/>, and
/// <see cref="RejectionReason"/> contract types.
/// </summary>
[Property("Category", "Unit")]
public class WorkClassTests
{
    /// <summary>
    /// Verifies the enum underlying values are ordered Interactive (0),
    /// Default (1), Batch (2) so comparisons read "lower = more urgent".
    /// </summary>
    [Test]
    public async Task WorkClass_Values_OrderedInteractiveDefaultBatch()
    {
        // Assert - explicit underlying values
        await Assert.That((int)WorkClass.Interactive).IsEqualTo(0);
        await Assert.That((int)WorkClass.Default).IsEqualTo(1);
        await Assert.That((int)WorkClass.Batch).IsEqualTo(2);

        // Assert - ordering reads "lower = more urgent"
        await Assert.That((int)WorkClass.Interactive).IsLessThan((int)WorkClass.Default);
        await Assert.That((int)WorkClass.Default).IsLessThan((int)WorkClass.Batch);
    }

    /// <summary>
    /// Verifies the Accepted sentinel reports acceptance and carries no
    /// rejection reason.
    /// </summary>
    [Test]
    public async Task EnqueueResult_Accepted_HasNoRejectionReason()
    {
        // Arrange
        var result = EnqueueResult.Accepted;

        // Assert
        await Assert.That(result.IsAccepted).IsTrue();
        await Assert.That(result.Reason).IsNull();
    }

    /// <summary>
    /// Verifies a rejected result reports non-acceptance and carries the
    /// rejection reason, for every defined reason.
    /// </summary>
    /// <param name="reason">The rejection reason under test.</param>
    [Test]
    [Arguments(RejectionReason.CapacityExceeded)]
    [Arguments(RejectionReason.WatermarkExceeded)]
    [Arguments(RejectionReason.Shutdown)]
    public async Task EnqueueResult_Rejected_CarriesReason(RejectionReason reason)
    {
        // Arrange
        var result = EnqueueResult.Rejected(reason);

        // Assert
        await Assert.That(result.IsAccepted).IsFalse();
        await Assert.That(result.Reason).IsEqualTo(reason);
    }
}
