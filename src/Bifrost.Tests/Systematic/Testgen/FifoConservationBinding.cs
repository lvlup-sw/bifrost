using System.Text.Json;

using Bifrost.Queues;

namespace Bifrost.Tests.Systematic.Testgen;

internal static class FifoConservationBinding
{
    public static bool IsValid(JsonElement input) => input.ValueKind == JsonValueKind.Array;

    public static Task<string[]> Observe(JsonElement input)
    {
        var queue = new FifoChannelWorkQueue<string>(Math.Max(1, input.GetArrayLength()));
        foreach (var value in input.EnumerateArray())
        {
            var item = value.GetString()!;
            if (!queue.TryEnqueue(item)) throw new InvalidOperationException("Unexpected admission failure.");
        }

        queue.Complete();
        var observed = new List<string>();
        while (queue.TryDequeue(out var item)) observed.Add(item);
        return Task.FromResult(observed.ToArray());
    }

    public static Task<bool> Predicate(JsonElement input, string[] observed) =>
        Task.FromResult(input.EnumerateArray().Select(value => value.GetString()!).SequenceEqual(observed, StringComparer.Ordinal));
}
