using Bifrost.Concurrency;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

static void Seed(int seed) => typeof(ThreadHandle).GetField("t_current", BindingFlags.NonPublic | BindingFlags.Static)!
    .SetValue(null, ThreadHandle.CreateForTesting((ulong)seed + 1, 2, 3, 4));

if (args.Contains("quality"))
{
    foreach (int population in new[] { 64, 4096 })
    foreach (int n in new[] { 4, 16 })
    foreach (int sticky in new[] { 1, 4 })
    foreach (int buffer in new[] { 0, 16 })
    foreach (int seed in new[] { 1, 2, 3, 4, 5 })
    {
        Seed(seed);
        const int operations = 12000;
        int count = population + operations;
        var random = new Random(seed);
        int[] keys = Enumerable.Range(0, count).ToArray();
        random.Shuffle(keys);
        var queue = new ConcurrentPriorityQueue<int, int>(subQueueCount: n, boundedCapacity: -1,
            comparer: null, stickiness: sticky, bufferCapacity: buffer);
        var live = new Fenwick(count + 1);
        var delays = new Fenwick(count + 1);
        var startDelay = new int[count];
        var ranks = new List<int>();
        var observedDelays = new List<int>();
        int next = 0;
        void Push()
        {
            int key = keys[next++];
            startDelay[key] = delays.Sum(key);
            live.Add(key, 1);
            queue.Enqueue(key, key);
        }
        void Pop()
        {
            if (!queue.TryDequeue(out _, out int key)) return;
            ranks.Add(live.Sum(key) - 1);
            observedDelays.Add(delays.Sum(key) - startDelay[key]);
            live.Add(key, -1);
            delays.Add(0, 1);
            delays.Add(key, -1);
        }
        for (int i = 0; i < population; i++) Push();
        for (int i = 0; i < operations; i++) { Push(); Pop(); }
        void Report(string phase)
        {
            ranks.Sort(); observedDelays.Sort();
            Console.WriteLine($"quality population={population} n={n} s={sticky} c={buffer} seed={seed} phase={phase} rankMean={ranks.Average():F6} rankP99={ranks[(int)(ranks.Count * .99)]} rankMax={ranks[^1]} delayMean={observedDelays.Average():F6} delayP99={observedDelays[(int)(observedDelays.Count * .99)]} delayMax={observedDelays[^1]}");
            ranks.Clear(); observedDelays.Clear();
        }
        Report("mixed");
        while (!queue.IsEmpty) Pop();
        Report("drain");
    }
    return;
}

if (args.Contains("trace") || args.Contains("trace-ties"))
{
    foreach (bool custom in new[] { false, true })
    foreach (int n in new[] { 1, 4, 16 })
    foreach (int s in new[] { 1, 4 })
    foreach (int c in new[] { 0, 1, 16 })
    {
        Seed(123);
        var q = new ConcurrentPriorityQueue<int, int>(subQueueCount: n, boundedCapacity: -1, comparer: custom ? Comparer<int>.Create((x, y) => x.CompareTo(y)) : null, stickiness: s, bufferCapacity: c);
        var output = new StringBuilder();
        var random = new Random(29);
        for (int i = 0; i < 12000; i++)
        {
            if (i < 2000 || random.Next(2) == 0) q.Enqueue(i, args.Contains("trace-ties") ? random.Next(8) : random.Next(100000) * 20000 + i);
            else if (q.TryDequeue(out int e, out int p)) output.Append(e).Append(':').Append(p).Append(';');
            foreach (var stripe in q.SubQueuesForTest)
            {
                stripe.TryReadTop(out var top, out var empty);
                output.Append('|').Append(stripe.VolatileCount).Append(':').Append(empty).Append(':').Append(top)
                    .Append(':').Append(stripe.InsertionCountForTest).Append(':').Append(stripe.DeletionCountForTest)
                    .Append(':').Append(stripe.DebugTopVersionForTest);
            }
        }
        foreach (var stripe in q.SubQueuesForTest)
        {
            stripe.TryReadTop(out var top, out var empty);
            output.Append('|').Append(stripe.VolatileCount).Append(':').Append(empty).Append(':').Append(top)
                .Append(':').Append(stripe.InsertionCountForTest).Append(':').Append(stripe.DeletionCountForTest)
                .Append(':').Append(stripe.DebugTopVersionForTest);
            var entries = new List<(int Element, int Priority)>();
            stripe.SnapshotTo(entries);
            foreach (var entry in entries) output.Append('/').Append(entry.Element).Append(':').Append(entry.Priority);
        }
        while (q.TryDequeue(out int e, out int p)) output.Append(e).Append(':').Append(p).Append(';');
        Console.WriteLine($"trace custom={custom} n={n} s={s} c={c} {Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output.ToString())))}");
    }
    return;
}

if (args.Contains("latency"))
{
    foreach (bool custom in new[] { false, true })
    foreach (int population in new[] { 16, 4096 })
    foreach (int buffer in new[] { 0, 16 })
    {
        IComparer<int>? comparer = custom ? Comparer<int>.Create((x, y) => x.CompareTo(y)) : null;
        Seed(73);
        var random = new Random(73);
        var keys = Enumerable.Range(0, 200000).Select(_ => random.Next()).ToArray();
        var queue = new ConcurrentPriorityQueue<int, int>(subQueueCount: 32, boundedCapacity: -1,
            comparer: comparer, bufferCapacity: buffer);
        var stripe = new SubQueue<int, int>(comparer, 0, new ulong[1], buffer);
        for (int i = 0; i < population; i++)
        {
            queue.Enqueue(i, keys[i]);
            stripe.TryLockedPush(i, keys[i]);
        }
        for (int i = 0; i < 100000; i++)
        {
            queue.Enqueue(i, keys[i]); queue.TryDequeue(out _, out _);
            stripe.TryLockedPush(i, keys[i]); stripe.TryLockedPop(out _, out _);
        }
        var publicTimes = new long[100000];
        var heldTimes = new long[100000];
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < publicTimes.Length; i++)
        {
            int key = keys[i + 100000];
            long start = Stopwatch.GetTimestamp();
            queue.Enqueue(i, key); queue.TryDequeue(out _, out _);
            publicTimes[i] = Stopwatch.GetTimestamp() - start;
            stripe.SyncLock.Enter();
            start = Stopwatch.GetTimestamp();
            // Reentrant push plus held-root pop: measures stripe mutation critical-section work,
            // excluding outer lock acquisition, routing and contention. Includes one inner lock.
            stripe.TryLockedPush(i, key); stripe.PopHeldRoot(out _, out _);
            heldTimes[i] = Stopwatch.GetTimestamp() - start;
            stripe.SyncLock.Exit();
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Array.Sort(publicTimes); Array.Sort(heldTimes);
        double Ns(long ticks) => ticks * 1e9 / Stopwatch.Frequency;
        Console.WriteLine($"latency custom={custom} population={population} c={buffer} pairMeanNs={publicTimes.Average() * 1e9 / Stopwatch.Frequency:F1} pairP50Ns={Ns(publicTimes[50000]):F1} pairP99Ns={Ns(publicTimes[99000]):F1} heldMeanNs={heldTimes.Average() * 1e9 / Stopwatch.Frequency:F1} heldP50Ns={Ns(heldTimes[50000]):F1} heldP99Ns={Ns(heldTimes[99000]):F1} allocated={bytes}");
    }
    return;
}

if (args.Contains("batch") || args.Contains("batch-sparse"))
{
    RunBatches<int>(i => i, "value", args.Contains("batch-sparse"));
    RunBatches<object>(i => new Payload(i), "reference", args.Contains("batch-sparse"));
    return;
}

foreach (int population in new[] { 16, 4096 })
foreach (int c in new[] { 0, 16 })
foreach (int threads in new[] { 1, 4 })
{
    if (args.Contains("dense") && (population != 4096 || c != 0 || threads != 4)) continue;
    if (args.Contains("buffered-single") && (c != 16 || threads != 1)) continue;
    var rates = new List<double>();
    long allocated = 0;
    for (int run = 0; run < 4; run++)
    {
        Seed(51);
        var q = new ConcurrentPriorityQueue<int, int>(subQueueCount: 32, boundedCapacity: -1, comparer: null, bufferCapacity: c);
        for (int i = 0; i < population; i++) q.Enqueue(i, i);
        using var start = new Barrier(threads + 1);
        long total = 0;
        long bytes = 0;
        var workers = Enumerable.Range(0, threads).Select(id => new Thread(() =>
        {
            Seed(id + 77);
            for (int i = 0; i < 10000; i++) { q.Enqueue(i, i); q.TryDequeue(out _, out _); }
            start.SignalAndWait();
            long before = GC.GetAllocatedBytesForCurrentThread();
            long stop = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            long count = 0;
            do
            {
                for (int i = 0; i < 256; i++) { q.Enqueue(i, i); q.TryDequeue(out _, out _); }
                count += 512;
            } while (Stopwatch.GetTimestamp() < stop);
            Interlocked.Add(ref total, count);
            Interlocked.Add(ref bytes, GC.GetAllocatedBytesForCurrentThread() - before);
        })).ToArray();
        foreach (var worker in workers) worker.Start();
        start.SignalAndWait();
        foreach (var worker in workers) worker.Join();
        if (run > 0) { rates.Add(total / 1e6); allocated = Math.Max(allocated, bytes); }
    }
    rates.Sort();
    Console.WriteLine($"perf population={population} c={c} threads={threads} Mops={rates[1]:F3} allocated={allocated}");
}

static void RunBatches<T>(Func<int, T> factory, string payload, bool sparseOnly)
{
    foreach (bool custom in new[] { false, true })
    foreach (int population in new[] { 16, 4096 })
    foreach (int buffer in new[] { 0, 16 })
    {
        if (sparseOnly && (!custom || population != 16 || buffer != 16)) continue;
        Seed(73);
        var random = new Random(73);
        const int batchSize = 32768, samples = 31;
        var keys = Enumerable.Range(0, batchSize).Select(_ => random.Next()).ToArray();
        var entries = Enumerable.Range(0, batchSize).Select(factory).ToArray();
        IComparer<int>? comparer = custom ? Comparer<int>.Create((x, y) => x.CompareTo(y)) : null;
        var queue = new ConcurrentPriorityQueue<T, int>(subQueueCount: 32, boundedCapacity: -1,
            comparer: comparer, bufferCapacity: buffer);
        var stripe = new SubQueue<T, int>(comparer, 0, new ulong[1], buffer);
        for (int i = 0; i < population; i++)
        {
            queue.Enqueue(entries[i], keys[i]); stripe.TryLockedPush(entries[i], keys[i]);
        }
        void QueueBatch()
        {
            for (int i = 0; i < batchSize; i++)
            {
                queue.Enqueue(entries[i], keys[i]); queue.TryDequeue(out _, out _);
            }
        }
        void StripeBatch()
        {
            for (int i = 0; i < batchSize; i++)
            {
                stripe.TryLockedPush(entries[i], keys[i]); stripe.TryLockedPop(out _, out _);
            }
        }
        for (int i = 0; i < 8; i++) { QueueBatch(); StripeBatch(); }
        var queueTimes = new double[samples];
        var stripeTimes = new double[samples];
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < samples; i++)
        {
            long start = Stopwatch.GetTimestamp(); QueueBatch();
            queueTimes[i] = (Stopwatch.GetTimestamp() - start) * 1e9 / Stopwatch.Frequency / batchSize;
            start = Stopwatch.GetTimestamp(); StripeBatch();
            stripeTimes[i] = (Stopwatch.GetTimestamp() - start) * 1e9 / Stopwatch.Frequency / batchSize;
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Array.Sort(queueTimes); Array.Sort(stripeTimes);
        Console.WriteLine($"batch payload={payload} custom={custom} population={population} c={buffer} pairMedianNs={queueTimes[samples / 2]:F2} pairP90Ns={queueTimes[(int)(samples * .9)]:F2} stripeMedianNs={stripeTimes[samples / 2]:F2} allocated={bytes}");
    }
}

sealed record Payload(int Value);

sealed class Fenwick(int count)
{
    private readonly int[] values = new int[count + 1];
    public void Add(int index, int delta)
    {
        for (int i = index + 1; i < values.Length; i += i & -i) values[i] += delta;
    }
    public int Sum(int index)
    {
        int sum = 0;
        for (int i = index + 1; i > 0; i -= i & -i) sum += values[i];
        return sum;
    }
}
