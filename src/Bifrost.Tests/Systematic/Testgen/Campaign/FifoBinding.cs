// =============================================================================
// <copyright file="FifoBinding.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Text.Json;

using Bifrost.Queues;

namespace Bifrost.Tests.Systematic.Testgen.Campaign;

/// <summary>
/// New non-arithmetic binding authored for issue #111: sequential conservation
/// of <see cref="FifoChannelWorkQueue{T}"/> for <see cref="string"/> items.
/// The oracle is the accepted FIFO contract itself — the drained sequence must
/// equal the accepted sequence element-for-element — not copied queue code.
/// </summary>
internal static class FifoBinding
{
    /// <summary>Checks the input is an array of strings within the declared domain.</summary>
    /// <param name="input">The generated case.</param>
    /// <returns><see langword="true"/> when the case is a string array.</returns>
    public static bool IsValid(JsonElement input)
        => input.ValueKind == JsonValueKind.Array
            && input.EnumerateArray().All(static item => item.ValueKind == JsonValueKind.String);

    /// <summary>
    /// Enqueues every item sequentially into a queue with capacity for the whole
    /// sequence, completes it, and drains it in dequeue order.
    /// </summary>
    /// <param name="input">The generated string array.</param>
    /// <returns>The drained sequence as a JSON array.</returns>
    public static Task<JsonElement> Observe(JsonElement input)
    {
        var items = input.EnumerateArray().Select(static item => item.GetString()!).ToArray();
        var queue = new FifoChannelWorkQueue<string>(Math.Max(1, items.Length));
        foreach (var item in items)
        {
            if (!queue.TryEnqueue(item))
            {
                throw new InvalidOperationException("TestGen observation: enqueue rejected within capacity.");
            }
        }

        queue.Complete();
        var drained = new List<string>(items.Length);
        while (queue.TryDequeue(out var item))
        {
            drained.Add(item);
        }

        if (queue.Count != 0)
        {
            throw new InvalidOperationException("TestGen observation: queue not empty after drain.");
        }

        return Task.FromResult(JsonSerializer.SerializeToElement(drained));
    }

    /// <summary>Checks the drained sequence equals the accepted sequence (ordinal).</summary>
    /// <param name="input">The generated string array.</param>
    /// <param name="observed">The drained sequence.</param>
    /// <returns><see langword="true"/> when both sequences are identical.</returns>
    public static Task<bool> Predicate(JsonElement input, JsonElement observed)
    {
        var expected = input.EnumerateArray().Select(static item => item.GetString()).ToArray();
        var actual = observed.EnumerateArray().Select(static item => item.GetString()).ToArray();
        return Task.FromResult(expected.SequenceEqual(actual, StringComparer.Ordinal));
    }
}
