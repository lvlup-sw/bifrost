using Bifrost.Concurrency;
using System.Diagnostics;
if (args.Contains("reference")) Run<object>(i => new Payload(i), "reference");
else Run<int>(i => i, "value");
static void Run<T>(Func<int, T> factory, string payload)
{
    const int batchSize = 32768, samples = 31;
    var random = new Random(73);
    var keys = Enumerable.Range(0, batchSize).Select(_ => random.Next()).ToArray();
    var entries = Enumerable.Range(0, batchSize).Select(factory).ToArray();
    var stripe = new SubQueue<T, int>(Comparer<int>.Create((x, y) => x.CompareTo(y)), 0, new ulong[1], 0);
    for (int i = 0; i < 4096; i++) stripe.TryLockedPush(entries[i], keys[i]);
    void StripeBatch()
    {
        for (int i = 0; i < batchSize; i++) { stripe.TryLockedPush(entries[i], keys[i]); stripe.TryLockedPop(out _, out _); }
    }
    for (int i = 0; i < 8; i++) StripeBatch();
    var times = new double[samples];
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < samples; i++)
    {
        long start = Stopwatch.GetTimestamp(); StripeBatch();
        times[i] = (Stopwatch.GetTimestamp() - start) * 1e9 / Stopwatch.Frequency / batchSize;
    }
    long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
    Array.Sort(times);
    Console.WriteLine($"stripe payload={payload} medianNs={times[samples / 2]:F2} p90Ns={times[(int)(samples * .9)]:F2} allocated={bytes}");
}
sealed record Payload(int Value);
