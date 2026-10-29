using Bifrost.Concurrency;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

static void Seed(int seed) => typeof(ThreadHandle).GetField("t_current", BindingFlags.NonPublic | BindingFlags.Static)!
    .SetValue(null, ThreadHandle.CreateForTesting((ulong)seed + 1, 2, 3, 4));

string scenario = args.FirstOrDefault() ?? "batch-value";
int seconds = args.Length > 1 ? int.Parse(args[1]) : 20;
if (scenario.StartsWith("batch-"))
{
    if (scenario == "batch-reference") Batch<object>(i => new Payload(i), seconds);
    else Batch<int>(i => i, seconds);
    return;
}
int population = scenario == "throughput-dense" ? 4096 : 16;
// Preserve the full validation matrix's preceding workloads and warmup order.
foreach (int p in new[] { 16, 4096 })
foreach (int c in new[] { 0, 16 })
foreach (int threads in new[] { 1, 4 })
{
    bool target = p == population && c == 16 && threads == 1;
    for (int run = 0; run < 4; run++)
    {
        Seed(51);
        var q = new ConcurrentPriorityQueue<int, int>(subQueueCount: 32, boundedCapacity: -1, comparer: null, bufferCapacity: c);
        for (int i = 0; i < p; i++) q.Enqueue(i, i);
        using var barrier = new Barrier(threads + 1);
        bool sampled = target && run > 0;
        var workers = Enumerable.Range(0, threads).Select(id => new Thread(() =>
        {
            Seed(id + 77);
            for (int i = 0; i < 10000; i++) { q.Enqueue(i, i); q.TryDequeue(out _, out _); }
            barrier.SignalAndWait();
            if (sampled) ThroughputTarget(q, seconds);
            else ThroughputWarmup(q, 1);
        })).ToArray();
        foreach (var worker in workers) worker.Start();
        barrier.SignalAndWait();
        foreach (var worker in workers) worker.Join();
    }
    if (target) return;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static void ThroughputTarget(ConcurrentPriorityQueue<int, int> q, int seconds) => ThroughputLoop(q, seconds);
[MethodImpl(MethodImplOptions.NoInlining)]
static void ThroughputWarmup(ConcurrentPriorityQueue<int, int> q, int seconds) => ThroughputLoop(q, seconds);
[MethodImpl(MethodImplOptions.NoInlining)]
static void ThroughputLoop(ConcurrentPriorityQueue<int, int> q, int seconds)
{
    long stop = Stopwatch.GetTimestamp() + Stopwatch.Frequency * seconds;
    do
    {
        for (int i = 0; i < 256; i++) { q.Enqueue(i, i); q.TryDequeue(out _, out _); }
    } while (Stopwatch.GetTimestamp() < stop);
}

static void Batch<T>(Func<int, T> factory, int seconds)
{
    Seed(73);
    const int batchSize = 32768;
    var random = new Random(73);
    var keys = Enumerable.Range(0, batchSize).Select(_ => random.Next()).ToArray();
    var entries = Enumerable.Range(0, batchSize).Select(factory).ToArray();
    var comparer = Comparer<int>.Create((x, y) => x.CompareTo(y));
    var queue = new ConcurrentPriorityQueue<T, int>(subQueueCount: 32, boundedCapacity: -1, comparer: comparer, bufferCapacity: 16);
    var stripe = new SubQueue<T, int>(comparer, 0, new ulong[1], 16);
    for (int i = 0; i < 16; i++) { queue.Enqueue(entries[i], keys[i]); stripe.TryLockedPush(entries[i], keys[i]); }
    for (int i = 0; i < 8; i++) { QueueBatch(queue, entries, keys); StripeBatch(stripe, entries, keys); }
    long stop = Stopwatch.GetTimestamp() + Stopwatch.Frequency * seconds;
    do { QueueBatch(queue, entries, keys); StripeBatch(stripe, entries, keys); } while (Stopwatch.GetTimestamp() < stop);
}
[MethodImpl(MethodImplOptions.NoInlining)]
static void QueueBatch<T>(ConcurrentPriorityQueue<T, int> q, T[] entries, int[] keys)
{
    for (int i = 0; i < keys.Length; i++) { q.Enqueue(entries[i], keys[i]); q.TryDequeue(out _, out _); }
}
[MethodImpl(MethodImplOptions.NoInlining)]
static void StripeBatch<T>(SubQueue<T, int> q, T[] entries, int[] keys)
{
    for (int i = 0; i < keys.Length; i++) { q.TryLockedPush(entries[i], keys[i]); q.TryLockedPop(out _, out _); }
}
sealed record Payload(int Value);
