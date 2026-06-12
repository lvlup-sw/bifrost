// =============================================================================
// <copyright file="AdmissionThresholds.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Runtime.CompilerServices;

using Bifrost.Core;

namespace Bifrost.Queues;

/// <summary>
/// Per-class admission threshold COUNTS for the watermark load-shedding policy
/// (DR-6), shared by the priority bindings
/// (<see cref="ConcurrentPriorityWorkQueue{TWork}"/> and
/// <see cref="LockingPriorityWorkQueue{TWork}"/>): an enqueue of a class is rejected
/// while the queue count is greater than or equal to the class's threshold.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Precompute"/> evaluates the floating-point
/// <c>watermarkFraction × capacity</c> products exactly once — mirroring
/// <see cref="PriorityKey.Precompute"/> for the boost windows — so the hot-path
/// admission check is a single integer comparison: no floating point, no allocation.
/// <c>floor()</c> semantics: a class admits while the count is strictly below its
/// threshold.
/// </para>
/// <para>
/// <see cref="Precompute"/> also validates the cross-property monotonicity
/// requirement (<c>Batch ≤ Default ≤ Interactive</c>) — a more urgent class must
/// never be shed before a less urgent one (DR-6). Validation lives here, at
/// consumption, because the relation cannot be a single-setter guard on
/// <see cref="PriorityDispatchOptions"/> without order-of-assignment traps.
/// </para>
/// </remarks>
internal readonly struct AdmissionThresholds
{
    /// <summary>
    /// The Batch admission threshold count — the lowest class, shed first (DR-6).
    /// </summary>
    private readonly long _batchThreshold;

    /// <summary>
    /// The Default admission threshold count — shed after Batch, before Interactive.
    /// </summary>
    private readonly long _defaultThreshold;

    /// <summary>
    /// The Interactive admission threshold count — the most urgent class, shed last;
    /// equals the hard capacity at the default 1.0 watermark fraction.
    /// </summary>
    private readonly long _interactiveThreshold;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdmissionThresholds"/> struct.
    /// Private: obtain instances via <see cref="Precompute"/>, which validates the
    /// monotonicity relation.
    /// </summary>
    /// <param name="batchThreshold">The Batch admission threshold count.</param>
    /// <param name="defaultThreshold">The Default admission threshold count.</param>
    /// <param name="interactiveThreshold">The Interactive admission threshold count.</param>
    private AdmissionThresholds(long batchThreshold, long defaultThreshold, long interactiveThreshold)
    {
        _batchThreshold = batchThreshold;
        _defaultThreshold = defaultThreshold;
        _interactiveThreshold = interactiveThreshold;
    }

    /// <summary>
    /// Validates the watermark monotonicity relation and converts the fractions into
    /// integer threshold counts, once per options + capacity pair.
    /// </summary>
    /// <param name="options">The priority dispatch options carrying the watermark fractions.</param>
    /// <param name="capacity">The bounded capacity of the consuming queue.</param>
    /// <returns>The precomputed per-class admission threshold counts.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="options"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the admission watermarks are not monotone non-decreasing with class
    /// urgency (<c>Batch ≤ Default ≤ Interactive</c>) — a more urgent class must never
    /// be shed before a less urgent one (DR-6).
    /// </exception>
    public static AdmissionThresholds Precompute(PriorityDispatchOptions options, int capacity)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.BatchAdmissionWatermark > options.DefaultAdmissionWatermark
            || options.DefaultAdmissionWatermark > options.InteractiveAdmissionWatermark)
        {
            throw new ArgumentException(
                "Admission watermarks must be monotone non-decreasing with class urgency: "
                + $"Batch ({options.BatchAdmissionWatermark}) <= Default "
                + $"({options.DefaultAdmissionWatermark}) <= Interactive "
                + $"({options.InteractiveAdmissionWatermark}).",
                nameof(options));
        }

        return new AdmissionThresholds(
            (long)(options.BatchAdmissionWatermark * capacity),
            (long)(options.DefaultAdmissionWatermark * capacity),
            (long)(options.InteractiveAdmissionWatermark * capacity));
    }

    /// <summary>
    /// Looks up the precomputed admission threshold count for the given work class
    /// (DR-6). Branch-light hot-path helper: a single switch over the three classes,
    /// no allocation, no floating point. Unrecognized values fall back to the Default
    /// threshold, mirroring <see cref="PriorityKey.Boosts.BoostTicksFor"/>.
    /// </summary>
    /// <param name="workClass">The work class seeking admission.</param>
    /// <returns>
    /// The threshold count: the class is rejected while the queue count is greater
    /// than or equal to this value.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ThresholdFor(WorkClass workClass)
        => workClass switch
        {
            WorkClass.Interactive => _interactiveThreshold,
            WorkClass.Batch => _batchThreshold,
            _ => _defaultThreshold,
        };
}
