// =============================================================================
// <copyright file="TerminalOutcomeRecord.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Outcomes;

/// <summary>The settled result of one started work item.</summary>
/// <param name="Outcome">The final outcome of the item.</param>
/// <param name="Attempts">The number of handler attempts the item used, at least one.</param>
/// <param name="Duration">The time from the start of the first attempt to the end of the last one.</param>
/// <param name="Capture">What the stage did with the item in the dead-letter queue.</param>
internal readonly record struct TerminalOutcomeRecord(
    TerminalOutcome Outcome,
    int Attempts,
    TimeSpan Duration,
    CaptureDisposition Capture);
