// =============================================================================
// <copyright file="CpqTuningProfile.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Core;

/// <summary>
/// Selects how a priority work queue tunes the relaxed MultiQueue it is built on. Each profile resolves
/// to a stickiness factor and a buffering capacity that the queue applies at construction. The trade is
/// always throughput and single-operation latency against dequeue-order accuracy; the right choice
/// depends on how many threads share the queue and on what its elements cost to move.
/// </summary>
public enum CpqTuningProfile
{
    /// <summary>
    /// The default. Keeps stickiness at its baseline and turns buffering on only when the element type
    /// carries managed references — where amortizing heap access pays for itself — and leaves it off for
    /// pure value-type elements. A sound choice when several worker threads share the queue.
    /// </summary>
    Balanced = 0,

    /// <summary>
    /// For a queue driven by only one or two threads, or by dequeue-heavy (drain-style) work, or where
    /// single-operation latency matters most. Raises stickiness so a thread amortizes its sampling cost,
    /// and turns buffering on. Both lift throughput in that regime, at the cost of a looser dequeue order.
    /// </summary>
    LowConcurrency,

    /// <summary>
    /// For callers who want the tightest dequeue order. Keeps stickiness at its baseline and leaves
    /// buffering off, so a pop stays as close to the true minimum as the relaxed queue allows — forgoing
    /// the throughput and latency that buffering would buy.
    /// </summary>
    StrictOrdering,
}
