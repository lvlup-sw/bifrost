// =============================================================================
// <copyright file="DispatchStrategyTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

using TUnit.Core;

namespace Bifrost.Tests.CoreTests;

/// <summary>
/// Tests for the <see cref="DispatchStrategy"/> enum members: the explicit orderings and the
/// keyed strategy.
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
    /// Verifies that <see cref="DispatchStrategy"/> declares only explicit orderings,
    /// with no sentinel member.
    /// </summary>
    /// <remarks>
    /// <c>PriorityRelaxed</c> must be distinct from <c>Fifo</c> and <c>PriorityExact</c>,
    /// <c>Keyed</c> must be distinct from all three, and the enum must declare exactly these 4
    /// members. Existing members keep their numeric values.
    /// </remarks>
    [Test]
    public async Task DispatchStrategy_DeclaresOnlyExplicitOrderings()
    {
        var priority = DispatchStrategy.PriorityRelaxed;
        await Assert.That(priority).IsNotEqualTo(DispatchStrategy.Fifo);
        await Assert.That(priority).IsNotEqualTo(DispatchStrategy.PriorityExact);
        await Assert.That(Enum.GetValues<DispatchStrategy>()).HasCount(4);
        await Assert.That(Enum.GetValues<DispatchStrategy>().Select(static s => (int)s)).IsEquivalentTo(new[] { 0, 1, 2, 3 });
    }
}
