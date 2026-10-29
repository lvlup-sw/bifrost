// =============================================================================
// <copyright file="WorkEnqueueOptions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Core;

/// <summary>Per-item metadata for admission.</summary>
/// <param name="Class">An explicit class, including Default; null uses the configured classifier.</param>
/// <param name="CorrelationId">Optional correlation metadata for admission events.</param>
public readonly record struct WorkEnqueueOptions(WorkClass? Class = null, string? CorrelationId = null)
{
    /// <summary>Converts an explicit class to admission options.</summary>
    /// <param name="workClass">The explicit class.</param>
    public static implicit operator WorkEnqueueOptions(WorkClass workClass) => new(workClass);
}
