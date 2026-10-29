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
}
