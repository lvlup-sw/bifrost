// =============================================================================
// <copyright file="PriorityKeyBinding.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Numerics;

using Bifrost.Core;
using Bifrost.Queues;

namespace Bifrost.Tests.Queues;

/// <summary>
/// Pure property-test bindings for <see cref="PriorityKey.Precompute"/> and
/// <see cref="PriorityKey.Compute{TWork}"/> over the 5-tuple
/// <c>(enqueuedAtTicks, interactiveWindowTicks, batchWindowTicks, timestampFrequency, workClass)</c>.
/// </summary>
public static class PriorityKeyBinding
{
    private const int TupleLength = 5;

    /// <summary>
    /// Checks whether <paramref name="input"/> lies within the supported signed-integer domain
    /// for priority virtual-time conversion and computation.
    /// </summary>
    /// <param name="input">
    /// Five 64-bit integers: <c>enqueuedAtTicks</c>, <c>interactiveWindowTicks</c>,
    /// <c>batchWindowTicks</c>, <c>timestampFrequency</c>, and <c>workClass</c>.
    /// </param>
    /// <returns><see langword="true"/> when <paramref name="input"/> satisfies the supported domain.</returns>
    public static bool Precondition(long[] input)
    {
        if (input is null || input.Length != TupleLength)
        {
            return false;
        }

        var interactiveWindowTicks = input[1];
        var batchWindowTicks = input[2];
        var timestampFrequency = input[3];
        var workClass = input[4];

        if (interactiveWindowTicks < 0 || batchWindowTicks < 0 || timestampFrequency <= 0)
        {
            return false;
        }

        if (workClass is < (long)WorkClass.Interactive or > (long)WorkClass.Batch)
        {
            return false;
        }

        var interactiveUnits = ((BigInteger)interactiveWindowTicks * timestampFrequency) / TimeSpan.TicksPerSecond;
        var batchUnits = ((BigInteger)batchWindowTicks * timestampFrequency) / TimeSpan.TicksPerSecond;
        return interactiveUnits <= long.MaxValue && batchUnits <= long.MaxValue;
    }

    /// <summary>
    /// Executes <see cref="PriorityKey.Precompute"/> and <see cref="PriorityKey.Compute{TWork}"/>
    /// for <paramref name="input"/> and returns the computed virtual-time priority key.
    /// </summary>
    /// <param name="input">
    /// Five 64-bit integers in the supported domain: <c>enqueuedAtTicks</c>,
    /// <c>interactiveWindowTicks</c>, <c>batchWindowTicks</c>, <c>timestampFrequency</c>,
    /// and <c>workClass</c>.
    /// </param>
    /// <returns>The effective priority key produced by <see cref="PriorityKey.Compute{TWork}"/>.</returns>
    public static long Observe(long[] input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length != TupleLength)
        {
            throw new ArgumentException("Expected a 5-element priority input tuple.", nameof(input));
        }

        var options = new PriorityDispatchOptions
        {
            InteractiveBoostWindow = TimeSpan.FromTicks(input[1]),
            BatchPenaltyWindow = TimeSpan.FromTicks(input[2]),
        };

        var boosts = PriorityKey.Precompute(options, input[3]);
        var envelope = new WorkEnvelope<int>(0, (WorkClass)input[4], input[0]);
        return PriorityKey.Compute(in envelope, in boosts);
    }

    /// <summary>
    /// Verifies that <paramref name="observed"/> equals the exact arbitrary-precision integer
    /// truncation and signed 64-bit saturation oracle for <paramref name="input"/>.
    /// </summary>
    /// <param name="input">The 5-element priority input tuple.</param>
    /// <param name="observed">The key returned by <see cref="Observe"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="observed"/> matches the exact oracle.</returns>
    public static bool Predicate(long[] input, long observed)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!Precondition(input))
        {
            return false;
        }

        BigInteger enqueuedAtTicks = input[0];
        var interactiveUnits = ((BigInteger)input[1] * input[3]) / TimeSpan.TicksPerSecond;
        var batchUnits = ((BigInteger)input[2] * input[3]) / TimeSpan.TicksPerSecond;

        var exactVirtualTime = input[4] switch
        {
            (long)WorkClass.Interactive => enqueuedAtTicks - interactiveUnits,
            (long)WorkClass.Default => enqueuedAtTicks,
            (long)WorkClass.Batch => enqueuedAtTicks + batchUnits,
            _ => throw new ArgumentOutOfRangeException(nameof(input), "Unsupported WorkClass value."),
        };

        var saturatedVirtualTime = BigInteger.Clamp(exactVirtualTime, long.MinValue, long.MaxValue);
        return observed == (long)saturatedVirtualTime;
    }
}
