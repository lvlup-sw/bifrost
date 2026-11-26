// =============================================================================
// <copyright file="CaptureDisposition.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Outcomes;

/// <summary>What the terminal outcome stage did with an item in the dead-letter queue.</summary>
internal enum CaptureDisposition
{
    /// <summary>No capture was attempted: the item did not fail, or no dead-letter queue is configured.</summary>
    None,

    /// <summary>The final failure was written to the dead-letter queue.</summary>
    Captured,

    /// <summary>The dead-letter queue threw while the final failure was written.</summary>
    CaptureFailed,
}
