#pragma warning disable CS1591, SA1600

using System.Collections.Concurrent;

using Bifrost.Concurrency;
using Bifrost.Tests.Systematic;

namespace Bifrost.Tests.Concurrency;

public class ObservedEmptyInvariantTests
{
    private sealed record Operation(int Actor, int Step, string Kind, long Start, long End, bool Success, int Id, int Priority);

    private sealed class History
    {
        private long clock;
        public ConcurrentQueue<Operation> Operations { get; } = new();
        public long Tick() => Interlocked.Increment(ref clock);
        public string Describe() => string.Join("\n", Operations.OrderBy(x => x.Start));
    }

    private static void Check(bool condition, string message, History history)
    {
        if (!condition) throw new InvalidOperationException(message + "\n" + history.Describe());
    }

    private static Operation Remove(ConcurrentPriorityQueue<int, int> queue, History history, int actor, int step)
    {
        long start = history.Tick();
        bool success = queue.TryDequeue(out int id, out int priority);
        var operation = new Operation(actor, step, "dequeue", start, history.Tick(), success, id, priority);
        history.Operations.Enqueue(operation);
        return operation;
    }

    [Test]
    public async Task Q5OverlappingProducersAndConsumersCannotExhaustPrefill()
    {
        foreach (int stripes in new[] { 1, 4, 128 })
        {
            foreach (int buffer in new[] { 0, 1, 16 })
            {
                for (int seed = 0; seed < InvariantRun.SeedCount(4); seed++)
                {
                    const int attempts = 48;
                    var queue = ConcurrentPriorityQueue<int, int>.Create(new() { SubQueueCount = stripes, BufferCapacity = buffer, Stickiness = 4 });
                    var history = new History();
                    var expected = Enumerable.Range(0, attempts + 1).ToDictionary(x => x, x => x % 3);
                    foreach (var item in expected) queue.Enqueue(item.Key, item.Value);
                    using var barrier = new Barrier(4);
                    var actors = Enumerable.Range(0, 4).Select(actor => Task.Factory.StartNew(() =>
                    {
                        var random = new Random(seed * 17 + actor);
                        for (int step = 0; step < 16; step++)
                        {
                            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException("actor barrier");
                            Thread.SpinWait(random.Next(128));
                            long start = history.Tick();
                            if (actor == 0)
                            {
                                int id = attempts + 1 + step;
                                queue.Enqueue(id, id % 3);
                                history.Operations.Enqueue(new(actor, step, "enqueue", start, history.Tick(), true, id, id % 3));
                            }
                            else
                            {
                                bool success = queue.TryDequeue(out int id, out int priority);
                                history.Operations.Enqueue(new(actor, step, "dequeue", start, history.Tick(), success, id, priority));
                            }
                        }
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
                    await Task.WhenAll(actors).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                    string context = $"Q5 seed={seed} stripes={stripes} buffer={buffer}";
                    foreach (var operation in history.Operations.Where(x => x.Kind == "enqueue")) expected.Add(operation.Id, operation.Priority);
                    foreach (var operation in history.Operations.Where(x => x.Kind == "dequeue"))
                    {
                        Check(operation.Success, context + " false despite an unremovable prefill survivor", history);
                        Check(expected.Remove(operation.Id, out int priority) && priority == operation.Priority, context + " duplicate, invented, or mismatched removal", history);
                    }

                    var snapshot = queue.ToArray();
                    Check(snapshot.Length == expected.Count && queue.Count == expected.Count, context + " quiescent count", history);
                    Check(snapshot.All(x => expected.Remove(x.Element, out int p) && p == x.Priority) && expected.Count == 0, context + " quiescent contents", history);
                    foreach (var actor in history.Operations.GroupBy(x => x.Actor))
                    {
                        var ordered = actor.OrderBy(x => x.Step).ToArray();
                        Check(ordered.Zip(ordered.Skip(1)).All(x => x.First.End < x.Second.Start), context + " actor order", history);
                    }
                }
            }
        }
    }

    [Test]
    public async Task Q5HeldSparseLastItemRequiresRetryAndThenSuccess()
    {
        foreach (int stripes in new[] { 1, 4, 128 })
        {
            var queue = ConcurrentPriorityQueue<int, int>.Create(new() { SubQueueCount = stripes });
            var history = new History();
            Check(queue.SubQueuesForTest[stripes - 1].TryLockedPush(73, 7), "prefill", history);
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var holder = Task.Factory.StartNew(() =>
            {
                lock (queue.SubQueuesForTest[stripes - 1].SyncLock)
                {
                    held.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("lock release");
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Task<Operation>? consumer = null;
            try
            {
                if (!held.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("lock acquisition");
                consumer = Task.Factory.StartNew(() => Remove(queue, history, 1, 0), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                if (!SpinWait.SpinUntil(() => queue.DebugScanEntryCountForTest > 0 || consumer.IsCompleted, TimeSpan.FromSeconds(5))) throw new TimeoutException("scan entry");
                await Task.WhenAny(consumer, Task.Delay(100)).ConfigureAwait(false);
            }
            finally
            {
                release.Set();
                await holder.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }

            var result = await consumer!.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Check(result.Success && result.Id == 73 && result.Priority == 7, "Q5 held populated stripe was treated as empty", history);
            Check(queue.DebugScanEntryCountForTest > 0, "Q5 authoritative scan was not reached", history);
            Check(!queue.TryDequeue(out _, out _), "Q5 quiescent empty result", history);
            Check(queue.DebugLastFalseScanEmptyObservationsForTest == stripes, "Q5 false needs a full empty pass", history);
            Check(queue.ToArray().Length == 0 && queue.Count == 0, "Q5 quiescent drain", history);
        }
    }

    private sealed class GatedComparer : IComparer<int>, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public int ProducerThread { get; set; }
        public int Compare(int x, int y)
        {
            if (Environment.CurrentManagedThreadId == ProducerThread)
            {
                Entered.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("comparer release");
            }

            return x.CompareTo(y);
        }

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }
    }

    [Test]
    public async Task Q5ProducerComparerOverlapsConsumerWithoutPermittingFalse()
    {
        using var comparer = new GatedComparer();
        var queue = ConcurrentPriorityQueue<int, int>.Create(new() { SubQueueCount = 1, BufferCapacity = 0, Comparer = comparer });
        var history = new History();
        queue.Enqueue(1, 1);
        var producer = Task.Factory.StartNew(() =>
        {
            comparer.ProducerThread = Environment.CurrentManagedThreadId;
            long start = history.Tick();
            queue.Enqueue(2, 2);
            history.Operations.Enqueue(new(0, 0, "enqueue", start, history.Tick(), true, 2, 2));
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task<Operation>? consumer = null;
        try
        {
            if (!comparer.Entered.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("comparer entry");
            consumer = Task.Factory.StartNew(() => Remove(queue, history, 1, 0), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            if (!SpinWait.SpinUntil(() => queue.DebugScanEntryCountForTest > 0 || consumer.IsCompleted, TimeSpan.FromSeconds(5))) throw new TimeoutException("scan entry");
            await Task.WhenAny(consumer, Task.Delay(100)).ConfigureAwait(false);
        }
        finally
        {
            comparer.Release.Set();
            await producer.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        var result = await consumer!.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Check(result.Success, "Q5 overlapping producer and consumer cannot erase the resident prefill", history);
        var insertion = history.Operations.Single(x => x.Kind == "enqueue");
        Check(insertion.Start < result.Start && result.Start < insertion.End, "Q5 controlled producer interval must contain consumer invocation", history);
        var expected = new Dictionary<int, int> { [1] = 1, [2] = 2 };
        Check(expected.Remove(result.Id, out int priority) && priority == result.Priority, "Q5 overlapping removal identity", history);
        var snapshot = queue.ToArray();
        Check(snapshot.Length == 1 && queue.Count == 1 && expected.Remove(snapshot[0].Element, out priority) && priority == snapshot[0].Priority, "Q5 overlapping quiescent remainder", history);
    }

    [Test]
    public async Task Q5CompetingLastItemAllowsOneSuccessAndOneObservedEmpty()
    {
        for (int seed = 0; seed < InvariantRun.SeedCount(16); seed++)
        {
            var queue = ConcurrentPriorityQueue<int, int>.Create(new() { SubQueueCount = 128 });
            var history = new History();
            Check(queue.SubQueuesForTest[127].TryLockedPush(seed, -seed), "prefill", history);
            using var barrier = new Barrier(2);
            var actors = Enumerable.Range(0, 2).Select(actor => Task.Factory.StartNew(() =>
            {
                if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException("last item barrier");
                long start = history.Tick();
                bool success = queue.TryDequeue(out int id, out int priority);
                history.Operations.Enqueue(new(actor, 0, "dequeue", start, history.Tick(), success, id, priority));
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            await Task.WhenAll(actors).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            var successes = history.Operations.Where(x => x.Success).ToArray();
            Check(successes.Length == 1 && successes[0].Id == seed && successes[0].Priority == -seed, "Q5 last item permitted histories require exactly one matching success", history);
            var empty = history.Operations.Single(x => !x.Success);
            Check(successes[0].Start < empty.End, "Q5 false cannot finish before the sole removal starts", history);
            Check(queue.DebugLastFalseScanEmptyObservationsForTest == 128, "Q5 last item false needs a full empty pass", history);
            Check(queue.ToArray().Length == 0 && queue.Count == 0, "Q5 last item quiescent conservation", history);
        }
    }
}
