// =============================================================================
// <copyright file="PriorityBindingTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

using TUnit.Core;

namespace Bifrost.Tests.CoreTests;

/// <summary>
/// Tests for the <see cref="PriorityBinding"/> enum (T1).
/// </summary>
[Property("Category", "Unit")]
public class PriorityBindingTests
{
    /// <summary>
    /// Verifies that <see cref="PriorityBinding.Auto"/> is the default value (0).
    /// </summary>
    [Test]
    public async Task PriorityBinding_DefaultValue_IsAuto()
    {
        // The default value of an enum in C# is 0, which must be Auto.
        var defaultValue = default(PriorityBinding);
        await Assert.That(defaultValue).IsEqualTo(PriorityBinding.Auto);
        // Verify Auto is value 0 (so default(PriorityBinding) == Auto, enum unset == Auto).
        var autoInt = (int)PriorityBinding.Auto;
        await Assert.That(autoInt).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies that <see cref="PriorityBinding"/> declares both
    /// <see cref="PriorityBinding.Locking"/> and <see cref="PriorityBinding.MultiQueue"/> members.
    /// </summary>
    [Test]
    public async Task PriorityBinding_DeclaresLockingAndMultiQueue()
    {
        // Both explicit override values must exist.
        var locking = PriorityBinding.Locking;
        var multiQueue = PriorityBinding.MultiQueue;

        await Assert.That(locking).IsNotEqualTo(PriorityBinding.Auto);
        await Assert.That(multiQueue).IsNotEqualTo(PriorityBinding.Auto);
        await Assert.That(locking).IsNotEqualTo(multiQueue);
    }
}
