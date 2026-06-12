// =============================================================================
// <copyright file="WorkClassTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

using TUnit.Core;

// NOTE: deliberately NOT "Bifrost.Tests.Core" — declaring that namespace would
// shadow the "Core.IWorkHandler<>" qualified reference in
// HealthChecks/DeadLetterQueueHealthCheckTests.cs (C# resolves "Core" against
// the nearest enclosing namespace, Bifrost.Tests, before Bifrost). These are
// contract-type tests, so they live in the existing Contracts namespace.
namespace Bifrost.Tests.Contracts;

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
        // Arrange - locals so the TUnit analyzer does not see constant operands
        var interactive = (int)WorkClass.Interactive;
        var defaultClass = (int)WorkClass.Default;
        var batch = (int)WorkClass.Batch;

        // Assert - explicit underlying values
        await Assert.That(interactive).IsEqualTo(0);
        await Assert.That(defaultClass).IsEqualTo(1);
        await Assert.That(batch).IsEqualTo(2);

        // Assert - ordering reads "lower = more urgent"
        await Assert.That(interactive).IsLessThan(defaultClass);
        await Assert.That(defaultClass).IsLessThan(batch);
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
