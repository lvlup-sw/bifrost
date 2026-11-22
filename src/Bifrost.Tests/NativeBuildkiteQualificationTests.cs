// Copyright (c) Levelup Software. All rights reserved.

namespace Bifrost.Tests;

/// <summary>
/// Disposable proof that the native Buildkite gate reports this revision’s test outcome.
/// </summary>
public sealed class NativeBuildkiteQualificationTests
{
    /// <summary>
    /// Verifies that the test runner reports the chosen fixture outcome.
    /// </summary>
    [Test]
    public async Task ReportsFixtureOutcome()
    {
        var observed = Math.Abs(-1);
        await Assert.That(observed).IsEqualTo(2);
    }
}
