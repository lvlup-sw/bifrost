// =============================================================================
// <copyright file="CoalesceCampaignBindings.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Globalization;
using System.Text.Json;

using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Internal;

namespace Bifrost.Tests.Scheduling.Testgen.Campaign;

/// <summary>
/// Shared observation/validity for the Coalesce campaign (issue #111).
/// Inputs are records <c>{last, now, interval}</c> of integer tick strings at
/// zero offset. Observation is the selected occurrence's UTC ticks as a string,
/// or <c>"-1"</c> when no occurrence is due.
/// </summary>
internal static class CoalesceObserve
{
    /// <summary>Maximum due occurrences the bounded enumerator will step through.</summary>
    internal const long MaxDueCount = 64;

    /// <summary>
    /// Validity: positive interval, at least one due occurrence, bounded due
    /// count, and one-interval translation/advance stays representable.
    /// </summary>
    /// <param name="input">The generated record.</param>
    /// <returns><see langword="true"/> when the record is in domain.</returns>
    public static bool IsValid(JsonElement input)
    {
        if (!TryRead(input, out var last, out var now, out var interval))
        {
            return false;
        }

        if (interval <= 0 || last < 0 || now <= last)
        {
            return false;
        }

        var due = (now - last) / interval;
        if (due < 1 || due > MaxDueCount)
        {
            return false;
        }

        // Advance (now + interval) and translate (both + interval) must stay
        // representable as DateTimeOffset ticks at zero offset.
        return now <= long.MaxValue - interval && last <= long.MaxValue - interval;
    }

    /// <summary>Observes the production Coalesce selection.</summary>
    /// <param name="input">The generated record.</param>
    /// <returns>The selected occurrence ticks, or <c>"-1"</c>.</returns>
    public static Task<JsonElement> Observe(JsonElement input)
    {
        TryRead(input, out var last, out var now, out var interval);
        var result = MissedFirePolicyApplier.ComputeMissedFires(
            new IntervalCadence(TimeSpan.FromTicks(interval)),
            new DateTimeOffset(last, TimeSpan.Zero),
            new DateTimeOffset(now, TimeSpan.Zero),
            MissedFirePolicy.Coalesce);
        var ticks = result.Count == 1 ? result[0].UtcTicks : -1L;
        return Task.FromResult(JsonSerializer.SerializeToElement(
            ticks.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>Reads the three tick fields.</summary>
    /// <param name="input">The generated record.</param>
    /// <param name="last">The last-fire ticks.</param>
    /// <param name="now">The query ticks.</param>
    /// <param name="interval">The interval ticks.</param>
    /// <returns><see langword="true"/> when all fields parse.</returns>
    internal static bool TryRead(JsonElement input, out long last, out long now, out long interval)
    {
        last = now = interval = 0;
        return input.ValueKind == JsonValueKind.Object
            && long.TryParse(input.GetProperty("last").GetString(), CultureInfo.InvariantCulture, out last)
            && long.TryParse(input.GetProperty("now").GetString(), CultureInfo.InvariantCulture, out now)
            && long.TryParse(input.GetProperty("interval").GetString(), CultureInfo.InvariantCulture, out interval);
    }

    /// <summary>Builds a record element from tick values.</summary>
    /// <param name="last">The last-fire ticks.</param>
    /// <param name="now">The query ticks.</param>
    /// <param name="interval">The interval ticks.</param>
    /// <returns>The record element.</returns>
    internal static JsonElement Record(long last, long now, long interval)
        => JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            ["last"] = last.ToString(CultureInfo.InvariantCulture),
            ["now"] = now.ToString(CultureInfo.InvariantCulture),
            ["interval"] = interval.ToString(CultureInfo.InvariantCulture),
        });
}

/// <summary>
/// Encoded metamorphic domain: <c>{steps, remainder, shift}</c> decodes to
/// <c>interval = 100000</c>, <c>last = 1000000 + shift·interval</c>,
/// <c>now = last + steps·interval + remainder</c>. Every structural combination
/// has 1–32 due occurrences, and the transforms below stay inside the domain
/// (they step <c>shift</c>/<c>steps</c> up, or down at the top edge), so no
/// generated source or follow-up is rejected.
/// </summary>
internal static class CoalesceEncoded
{
    /// <summary>The fixed interval in ticks.</summary>
    internal const long Interval = 100000;

    /// <summary>Checks the encoded fields are in range.</summary>
    /// <param name="input">The generated record.</param>
    /// <returns><see langword="true"/> when in domain.</returns>
    public static bool IsValid(JsonElement input)
        => TryRead(input, out var steps, out var remainder, out var shift)
            && steps is >= 1 and <= 32 && remainder is >= 0 and <= 99999 && shift is >= 0 and <= 10;

    /// <summary>Observes the production selection for the decoded instants.</summary>
    /// <param name="input">The generated record.</param>
    /// <returns>The selected occurrence.</returns>
    public static Task<JsonElement> Observe(JsonElement input)
    {
        TryRead(input, out var steps, out var remainder, out var shift);
        var last = 1000000 + (shift * Interval);
        var now = last + (steps * Interval) + remainder;
        var result = MissedFirePolicyApplier.ComputeMissedFires(
            new IntervalCadence(TimeSpan.FromTicks(Interval)),
            new DateTimeOffset(last, TimeSpan.Zero),
            new DateTimeOffset(now, TimeSpan.Zero),
            MissedFirePolicy.Coalesce);
        var ticks = result.Count == 1 ? result[0].UtcTicks : -1L;
        return Task.FromResult(JsonSerializer.SerializeToElement(
            ticks.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>Reads the encoded fields.</summary>
    /// <param name="input">The generated record.</param>
    /// <param name="steps">The due-step count.</param>
    /// <param name="remainder">The remainder ticks past the latest step.</param>
    /// <param name="shift">The translation shift in intervals.</param>
    /// <returns><see langword="true"/> when all fields parse.</returns>
    internal static bool TryRead(JsonElement input, out long steps, out long remainder, out long shift)
    {
        steps = remainder = shift = 0;
        return input.ValueKind == JsonValueKind.Object
            && long.TryParse(input.GetProperty("steps").GetString(), CultureInfo.InvariantCulture, out steps)
            && long.TryParse(input.GetProperty("remainder").GetString(), CultureInfo.InvariantCulture, out remainder)
            && long.TryParse(input.GetProperty("shift").GetString(), CultureInfo.InvariantCulture, out shift);
    }

    /// <summary>Builds an encoded record.</summary>
    /// <param name="steps">The due-step count.</param>
    /// <param name="remainder">The remainder ticks.</param>
    /// <param name="shift">The translation shift.</param>
    /// <returns>The record element.</returns>
    internal static JsonElement Record(long steps, long remainder, long shift)
        => JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            ["steps"] = steps.ToString(CultureInfo.InvariantCulture),
            ["remainder"] = remainder.ToString(CultureInfo.InvariantCulture),
            ["shift"] = shift.ToString(CultureInfo.InvariantCulture),
        });
}

/// <summary>Metamorphic binding: translating both timestamps by one interval.</summary>
internal static class CoalesceTranslateBinding
{
    /// <summary>Checks domain validity.</summary>
    /// <param name="input">The generated record.</param>
    /// <returns><see langword="true"/> when in domain.</returns>
    public static bool IsValid(JsonElement input) => CoalesceEncoded.IsValid(input);

    /// <summary>Observes the production selection.</summary>
    /// <param name="input">The generated record.</param>
    /// <returns>The selected occurrence.</returns>
    public static Task<JsonElement> Observe(JsonElement input) => CoalesceEncoded.Observe(input);

    /// <summary>Translates both timestamps by Δ = ±one interval (down at the top edge).</summary>
    /// <param name="input">The source record.</param>
    /// <returns>The translated record.</returns>
    public static Task<JsonElement> Transform(JsonElement input)
    {
        CoalesceEncoded.TryRead(input, out var steps, out var remainder, out var shift);
        return Task.FromResult(CoalesceEncoded.Record(steps, remainder, shift < 10 ? shift + 1 : shift - 1));
    }

    /// <summary>Checks the selected occurrence translated by the same Δ as the inputs.</summary>
    /// <param name="source">The source record.</param>
    /// <param name="follow">The translated record.</param>
    /// <param name="before">The source observation.</param>
    /// <param name="after">The follow-up observation.</param>
    /// <returns><see langword="true"/> when the relation holds.</returns>
    public static Task<bool> Relation(JsonElement source, JsonElement follow, JsonElement before, JsonElement after)
    {
        CoalesceEncoded.TryRead(source, out _, out _, out var shift);
        CoalesceEncoded.TryRead(follow, out _, out _, out var followShift);
        var delta = (followShift - shift) * CoalesceEncoded.Interval;
        var b = long.Parse(before.GetString()!, CultureInfo.InvariantCulture);
        var a = long.Parse(after.GetString()!, CultureInfo.InvariantCulture);
        return Task.FromResult(b >= 0 && delta != 0 && a - b == delta);
    }
}

/// <summary>Metamorphic binding: advancing the query by one interval.</summary>
internal static class CoalesceAdvanceBinding
{
    /// <summary>Checks domain validity.</summary>
    /// <param name="input">The generated record.</param>
    /// <returns><see langword="true"/> when in domain.</returns>
    public static bool IsValid(JsonElement input) => CoalesceEncoded.IsValid(input);

    /// <summary>Observes the production selection.</summary>
    /// <param name="input">The generated record.</param>
    /// <returns>The selected occurrence.</returns>
    public static Task<JsonElement> Observe(JsonElement input) => CoalesceEncoded.Observe(input);

    /// <summary>Advances the query by one interval (retreats at the top edge).</summary>
    /// <param name="input">The source record.</param>
    /// <returns>The advanced record.</returns>
    public static Task<JsonElement> Transform(JsonElement input)
    {
        CoalesceEncoded.TryRead(input, out var steps, out var remainder, out var shift);
        return Task.FromResult(CoalesceEncoded.Record(steps < 32 ? steps + 1 : steps - 1, remainder, shift));
    }

    /// <summary>Checks the selected latest occurrence moved by the query step.</summary>
    /// <param name="source">The source record.</param>
    /// <param name="follow">The advanced record.</param>
    /// <param name="before">The source observation.</param>
    /// <param name="after">The follow-up observation.</param>
    /// <returns><see langword="true"/> when the relation holds.</returns>
    public static Task<bool> Relation(JsonElement source, JsonElement follow, JsonElement before, JsonElement after)
    {
        CoalesceEncoded.TryRead(source, out var steps, out _, out _);
        CoalesceEncoded.TryRead(follow, out var followSteps, out _, out _);
        var delta = (followSteps - steps) * CoalesceEncoded.Interval;
        var b = long.Parse(before.GetString()!, CultureInfo.InvariantCulture);
        var a = long.Parse(after.GetString()!, CultureInfo.InvariantCulture);
        return Task.FromResult(b >= 0 && delta != 0 && a - b == delta);
    }
}

/// <summary>
/// Differential binding: a separately authored bounded occurrence enumerator.
/// It steps one interval at a time from <c>last</c> and keeps the latest
/// occurrence at or before <c>now</c>; it never calls the production selector.
/// </summary>
internal static class CoalesceDifferentialBinding
{
    /// <summary>Checks domain validity.</summary>
    /// <param name="input">The generated record.</param>
    /// <returns><see langword="true"/> when in domain.</returns>
    public static bool IsValid(JsonElement input) => CoalesceObserve.IsValid(input);

    /// <summary>Observes the production selection.</summary>
    /// <param name="input">The generated record.</param>
    /// <returns>The selected occurrence.</returns>
    public static Task<JsonElement> Observe(JsonElement input) => CoalesceObserve.Observe(input);

    /// <summary>Computes the expected latest occurrence by enumeration.</summary>
    /// <param name="input">The generated record.</param>
    /// <returns>The expected occurrence ticks.</returns>
    public static Task<JsonElement> Reference(JsonElement input)
    {
        CoalesceObserve.TryRead(input, out var last, out var now, out var interval);
        long latest = -1;
        for (var candidate = last + interval; candidate <= now; candidate += interval)
        {
            latest = candidate;
            if (candidate > now - interval)
            {
                break; // next step would pass now (and avoids overflow past it)
            }
        }

        return Task.FromResult(JsonSerializer.SerializeToElement(
            latest.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>Checks production and reference agree exactly.</summary>
    /// <param name="input">The generated record.</param>
    /// <param name="actual">The production observation.</param>
    /// <param name="expected">The reference observation.</param>
    /// <returns><see langword="true"/> when both are the same occurrence.</returns>
    public static Task<bool> Equivalent(JsonElement input, JsonElement actual, JsonElement expected)
        => Task.FromResult(string.Equals(actual.GetString(), expected.GetString(), StringComparison.Ordinal));
}
