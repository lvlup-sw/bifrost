// =============================================================================
// <copyright file="WorkClass.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Core;

/// <summary>
/// Classifies work for priority dispatch. Lower underlying values are more
/// urgent, so comparisons read "lower = more urgent".
/// </summary>
/// <remarks>
/// <para>
/// This enum only establishes the ordering contract between classes:
/// <see cref="Interactive"/> (0) dispatches ahead of <see cref="Default"/> (1),
/// which dispatches ahead of <see cref="Batch"/> (2). Virtual-time boost
/// semantics — aging queued entries so lower-priority classes are not starved
/// under sustained higher-priority load — land in a later stage of the
/// priority dispatch work and are layered on top of this ordering.
/// </para>
/// <para>
/// <see cref="Default"/> is the neutral middle class, used when callers do
/// not specify a work class.
/// </para>
/// </remarks>
public enum WorkClass
{
    /// <summary>
    /// Latency-sensitive work, dispatched ahead of all other classes.
    /// </summary>
    Interactive = 0,

    /// <summary>
    /// The neutral middle class, used when no work class is specified.
    /// </summary>
    Default = 1,

    /// <summary>
    /// Throughput-oriented work, dispatched after all other classes.
    /// </summary>
    Batch = 2,
}
