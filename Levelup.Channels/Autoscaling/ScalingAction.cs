// =============================================================================
// <copyright file="ScalingAction.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Levelup.Channels.Autoscaling;

/// <summary>
/// Represents the type of scaling action to perform.
/// </summary>
public enum ScalingAction
{
    /// <summary>
    /// No scaling action is needed.
    /// </summary>
    None = 0,

    /// <summary>
    /// Add workers to handle increased load.
    /// </summary>
    ScaleUp = 1,

    /// <summary>
    /// Remove workers to reduce resource usage.
    /// </summary>
    ScaleDown = 2,
}
