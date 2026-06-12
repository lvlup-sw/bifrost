// =============================================================================
// <copyright file="PriorityDispatchOptions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

namespace Bifrost.Queues;

/// <summary>
/// Configuration options for class-based priority dispatch (design DR-5).
/// </summary>
/// <remarks>
/// <para>
/// These windows parameterize the WFQ-style virtual-time priority key computed by
/// <see cref="PriorityKey"/>: <c>effectivePriority = EnqueuedAtTicks − Boost(Class)</c>,
/// where smaller = sooner. <see cref="InteractiveBoostWindow"/> is how far "back in
/// time" Interactive work is credited; <see cref="BatchPenaltyWindow"/> is how far
/// "forward in time" Batch work is deferred. Default work is never adjusted.
/// </para>
/// <para>
/// The boost window doubles as the starvation bound, by construction: any item that
/// has waited longer than <see cref="InteractiveBoostWindow"/> outranks a freshly
/// enqueued Interactive item — no aging scans or re-scoring required.
/// </para>
/// <para>
/// Validation uses throwing setters rather than <c>[Range]</c> attributes because
/// the <see cref="System.ComponentModel.DataAnnotations.RangeAttribute"/> overload
/// required for <see cref="TimeSpan"/> relies on TypeConverter-based string
/// conversion, which is not trim/AOT-clean under this project's analyzer set.
/// </para>
/// </remarks>
public class PriorityDispatchOptions
{
    private TimeSpan _interactiveBoostWindow = TimeSpan.FromSeconds(30);
    private TimeSpan _batchPenaltyWindow;

    /// <summary>
    /// Gets or sets the virtual-time credit applied to Interactive work.
    /// </summary>
    /// <value>The interactive boost window. Default is 30 seconds.</value>
    /// <remarks>
    /// An Interactive item dispatches ahead of Default or Batch items enqueued up to
    /// this window earlier. Equivalently, this is the maximum extra wait the boost can
    /// impose on lower-class work — the starvation bound. Must be non-negative;
    /// <see cref="TimeSpan.Zero"/> disables the boost entirely.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the assigned value is negative.
    /// </exception>
    public TimeSpan InteractiveBoostWindow
    {
        get => _interactiveBoostWindow;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            _interactiveBoostWindow = value;
        }
    }

    /// <summary>
    /// Gets or sets the virtual-time penalty applied to Batch work.
    /// </summary>
    /// <value>
    /// The batch penalty window. Default is <see cref="TimeSpan.Zero"/> — Batch is
    /// unboosted, not penalized, by default.
    /// </value>
    /// <remarks>
    /// When configured greater than zero, the Batch boost is the negation of this
    /// window, so the key grows by the penalty: Batch items behave as if enqueued
    /// this much later than they actually were. Must be non-negative.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the assigned value is negative.
    /// </exception>
    public TimeSpan BatchPenaltyWindow
    {
        get => _batchPenaltyWindow;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            _batchPenaltyWindow = value;
        }
    }
}
