// =============================================================================
// <copyright file="QueueFullMode.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Core;

/// <summary>The asynchronous admission policy when no space is available.</summary>
public enum QueueFullMode
{
    /// <summary>Wait for space. Supported only with FIFO ordering.</summary>
    Wait,

    /// <summary>Return an immediate admission rejection.</summary>
    Reject,
}
