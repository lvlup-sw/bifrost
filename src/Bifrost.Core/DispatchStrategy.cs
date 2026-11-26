// =============================================================================
// <copyright file="DispatchStrategy.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Core;

/// <summary>The ordering policy used to dispatch admitted items.</summary>
public enum DispatchStrategy
{
    /// <summary>First in, first out.</summary>
    Fifo,

    /// <summary>Exact minimum virtual-time priority under one lock.</summary>
    PriorityExact,

    /// <summary>MultiQueue relaxed two-choice priority ordering.</summary>
    PriorityRelaxed,

    /// <summary>
    /// One FIFO per work key, with ready keys served round robin and a limit on how many items of
    /// one key run at the same time.
    /// </summary>
    /// <remarks>
    /// Select it with <c>WithKeyedDispatch</c> on the orchestrator builder, which supplies the key
    /// selector and the per-key limits. Setting this value without <c>WithKeyedDispatch</c> is a
    /// configuration error. Keyed dispatch admits with <see cref="QueueFullMode.Reject"/> only and
    /// cannot be combined with class priority.
    /// </remarks>
    Keyed,
}
