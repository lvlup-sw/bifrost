// =============================================================================
// <copyright file="PriorityDispatchOptionsBindingTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;

using TUnit.Core;

namespace Bifrost.Tests.CoreTests;

/// <summary>
/// Tests for the <see cref="PriorityDispatchOptions.Binding"/> property (T3).
/// </summary>
[Property("Category", "Unit")]
public class PriorityDispatchOptionsBindingTests
{
    /// <summary>
    /// Verifies that <see cref="PriorityDispatchOptions.Binding"/> defaults to
    /// <see cref="PriorityBinding.Auto"/> on a freshly constructed options instance.
    /// </summary>
    [Test]
    public async Task PriorityDispatchOptions_Binding_DefaultsToAuto()
    {
        var options = new PriorityDispatchOptions();
        await Assert.That(options.Binding).IsEqualTo(PriorityBinding.Auto);
    }
}
