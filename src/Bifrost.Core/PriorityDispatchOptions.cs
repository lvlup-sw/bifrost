// =============================================================================
// <copyright file="PriorityDispatchOptions.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Runtime.CompilerServices;

namespace Bifrost.Core;

/// <summary>
/// Configuration options for class-based priority dispatch (designs DR-5 and DR-6).
/// </summary>
/// <remarks>
/// <para>
/// These windows parameterize the WFQ-style virtual-time priority key computed by
/// the queue layer's <c>PriorityKey</c>:
/// <c>effectivePriority = EnqueuedAtTicks − Boost(Class)</c>,
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
/// <b>Admission watermarks (DR-6).</b> The watermark fractions parameterize the
/// complementary admission-side policy: a bounded priority queue sheds the LOWEST
/// class FIRST at admission — Batch is rejected once the queue count reaches
/// <see cref="BatchAdmissionWatermark"/> × capacity, Default at
/// <see cref="DefaultAdmissionWatermark"/> × capacity, and Interactive is admitted up
/// to <see cref="InteractiveAdmissionWatermark"/> × capacity (the full queue by
/// default) — the WRED / priority-load-shedding precedent, with no eviction
/// machinery. The two knobs are complementary, not overlapping: watermarks decide the
/// admission-side shed order under pressure; the virtual-time key's bounded boost
/// window provides the dequeue-side starvation bound for whatever was admitted.
/// </para>
/// <para>
/// Each watermark fraction must lie in <c>(0, 1]</c>, enforced by its setter. The
/// cross-property monotonicity requirement — fractions non-decreasing with class
/// urgency, <c>Batch ≤ Default ≤ Interactive</c>, so a more urgent class is never
/// shed before a less urgent one — cannot be a single-setter guard without
/// order-of-assignment traps, and is therefore validated where the options are
/// consumed (the priority queue binding's constructor).
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
    private double _batchAdmissionWatermark = 0.90;
    private double _defaultAdmissionWatermark = 0.95;
    private double _interactiveAdmissionWatermark = 1.0;

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

    /// <summary>
    /// Gets or sets the admission watermark for Batch work, as a fraction of queue
    /// capacity.
    /// </summary>
    /// <value>The Batch admission fraction in <c>(0, 1]</c>. Default is 0.90.</value>
    /// <remarks>
    /// Batch enqueues are rejected once the queue count reaches this fraction of
    /// capacity — the first class shed under pressure (DR-6). Must not exceed
    /// <see cref="DefaultAdmissionWatermark"/>; the monotonicity relation is validated
    /// where the options are consumed (see the class remarks).
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the assigned value is not in <c>(0, 1]</c>.
    /// </exception>
    public double BatchAdmissionWatermark
    {
        get => _batchAdmissionWatermark;
        set
        {
            ThrowIfNotInUnitInterval(value);
            _batchAdmissionWatermark = value;
        }
    }

    /// <summary>
    /// Gets or sets the admission watermark for Default work, as a fraction of queue
    /// capacity.
    /// </summary>
    /// <value>The Default admission fraction in <c>(0, 1]</c>. Default is 0.95.</value>
    /// <remarks>
    /// Default enqueues are rejected once the queue count reaches this fraction of
    /// capacity — shed after Batch but before Interactive (DR-6). Must lie between
    /// <see cref="BatchAdmissionWatermark"/> and
    /// <see cref="InteractiveAdmissionWatermark"/>; the monotonicity relation is
    /// validated where the options are consumed (see the class remarks).
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the assigned value is not in <c>(0, 1]</c>.
    /// </exception>
    public double DefaultAdmissionWatermark
    {
        get => _defaultAdmissionWatermark;
        set
        {
            ThrowIfNotInUnitInterval(value);
            _defaultAdmissionWatermark = value;
        }
    }

    /// <summary>
    /// Gets or sets the admission watermark for Interactive work, as a fraction of
    /// queue capacity.
    /// </summary>
    /// <value>
    /// The Interactive admission fraction in <c>(0, 1]</c>. Default is 1.0 —
    /// Interactive work is admitted all the way to hard capacity.
    /// </value>
    /// <remarks>
    /// The most urgent class is shed last (DR-6); at the default of 1.0 only hard
    /// capacity exhaustion rejects Interactive work. Must not be less than
    /// <see cref="DefaultAdmissionWatermark"/>; the monotonicity relation is validated
    /// where the options are consumed (see the class remarks).
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the assigned value is not in <c>(0, 1]</c>.
    /// </exception>
    public double InteractiveAdmissionWatermark
    {
        get => _interactiveAdmissionWatermark;
        set
        {
            ThrowIfNotInUnitInterval(value);
            _interactiveAdmissionWatermark = value;
        }
    }

    /// <summary>
    /// Gets or sets the tuning profile for the relaxed MultiQueue the priority queue is built on.
    /// </summary>
    /// <value>The selected profile. Default is <see cref="CpqTuningProfile.Balanced"/>.</value>
    /// <remarks>
    /// <see cref="CpqTuningProfile.Balanced"/> keeps the dequeue contract as tight as the relaxed queue
    /// allows and turns buffering on for reference-bearing work items, where it pays for itself; it suits
    /// a queue shared across several worker threads. Choose <see cref="CpqTuningProfile.LowConcurrency"/>
    /// for a queue driven by only one or two threads or by drain-style work, or
    /// <see cref="CpqTuningProfile.StrictOrdering"/> when dequeue-order accuracy matters more than
    /// throughput.
    /// </remarks>
    public CpqTuningProfile CpqTuning { get; set; } = CpqTuningProfile.Balanced;

    /// <summary>
    /// Validates that a watermark assignment lies in <c>(0, 1]</c>, rejecting <see cref="double.NaN"/>
    /// explicitly. <c>NaN</c> fails every ordering comparison, so the bare
    /// <c>ThrowIfNegativeOrZero</c> / <c>ThrowIfGreaterThan</c> pair would let it slip through and
    /// silently destabilize admission. Centralized so all three setters stay in step.
    /// </summary>
    /// <param name="value">The assigned watermark fraction.</param>
    /// <param name="paramName">The captured argument name, for the thrown exception.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="value"/> is <c>NaN</c> or outside <c>(0, 1]</c>.
    /// </exception>
    private static void ThrowIfNotInUnitInterval(double value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (double.IsNaN(value))
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Watermark must be a number in (0, 1].");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, paramName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 1.0, paramName);
    }
}
