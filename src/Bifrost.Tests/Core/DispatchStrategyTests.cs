// =============================================================================
// <copyright file="DispatchStrategyTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

using TUnit.Core;

namespace Bifrost.Tests.CoreTests;

/// <summary>
/// Tests for the <see cref="DispatchStrategy"/> enum additions (T2):
/// specifically the new <c>Priority</c> sentinel member.
/// </summary>
[Property("Category", "Unit")]
public class DispatchStrategyTests
{
    /// <summary>
    /// Verifies that <see cref="DispatchStrategy.Fifo"/> is the default value (0),
    /// preserving the pre-existing semantics as the default strategy.
    /// </summary>
    [Test]
    public async Task DispatchStrategy_DefaultValue_IsFifo()
    {
        var defaultValue = default(DispatchStrategy);
        await Assert.That(defaultValue).IsEqualTo(DispatchStrategy.Fifo);
        var fifoInt = (int)DispatchStrategy.Fifo;
        await Assert.That(fifoInt).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies that <see cref="DispatchStrategy"/> declares a <c>Priority</c>
    /// sentinel member, distinct from all other members.
    /// </summary>
    [Test]
    public async Task DispatchStrategy_DeclaresPrioritySentinel()
    {
        // Priority must exist and be distinct from all concrete strategies.
        var priority = DispatchStrategy.Priority;
        await Assert.That(priority).IsNotEqualTo(DispatchStrategy.Fifo);
        await Assert.That(priority).IsNotEqualTo(DispatchStrategy.PriorityLocking);
        await Assert.That(priority).IsNotEqualTo(DispatchStrategy.PriorityMultiQueue);
    }
}
