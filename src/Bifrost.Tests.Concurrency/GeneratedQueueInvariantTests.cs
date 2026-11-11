#pragma warning disable CS1591, SA1600

using System.Collections.Concurrent;

using Bifrost.Concurrency;

namespace Bifrost.Tests.Concurrency;

public class GeneratedQueueInvariantTests
{
    private readonly record struct Item(int Id, int Value);
    private sealed record RefPriority(int Value);
    private sealed class Fault : Exception;
    private sealed class FaultComparer : IComparer<int>
    {
        public int Calls;
        public int ThrowAt = int.MaxValue;
        public int Compare(int x, int y)
        {
            if (++Calls == ThrowAt) throw new Fault();
            return x.CompareTo(y);
        }
    }

    private static void Check(bool value, string context)
    {
        if (!value) throw new InvalidOperationException(context);
    }

    private static ConcurrentPriorityQueue<Item, int> Create(int capacity, int stripes, int buffer, IComparer<int>? comparer = null) =>
        ConcurrentPriorityQueue<Item, int>.Create(new() { Capacity = capacity, SubQueueCount = stripes, BufferCapacity = buffer, Comparer = comparer });

    private static void Snapshot(ConcurrentPriorityQueue<Item, int> queue, Dictionary<Item, int> live, string context)
    {
        var actual = queue.ToArray();
        Check(queue.Count == live.Count && actual.Length == live.Count, context + " count");
        Check(actual.Select(x => x.Element).Distinct().Count() == actual.Length, context + " duplicate");
        Check(actual.All(x => live.TryGetValue(x.Element, out int p) && p == x.Priority), context + " contents");
    }

    [Test]
    public void Q1GeneratedSuccessfulEffectsConserveIncludingClear()
    {
        for (int seed = 0; seed < 32; seed++)
        {
            foreach (int buffer in new[] { 0, 1, 16 })
            {
                var random = new Random(seed);
                var queue = Create(33, 4, buffer);
                var live = new Dictionary<Item, int>();
                int accepted = 0, removed = 0, cleared = 0;
                for (int step = 0; step < 300; step++)
                {
                    string context = $"Q1 seed={seed} buffer={buffer} step={step}";
                    int operation = random.Next(10);
                    if (operation < 6)
                    {
                        var item = new Item(step, random.Next(3));
                        int priority = random.Next(-3, 4);
                        if (queue.TryEnqueue(item, priority))
                        {
                            live.Add(item, priority);
                            accepted++;
                        }
                    }
                    else if (operation < 9)
                    {
                        if (queue.TryDequeue(out var item, out int priority))
                        {
                            Check(live.Remove(item, out int expected) && expected == priority, context + " removal");
                            removed++;
                        }
                        else
                        {
                            Check(live.Count == 0, context + " quiescent false");
                        }
                    }
                    else
                    {
                        cleared += live.Count;
                        live.Clear();
                        queue.Clear();
                    }

                    Snapshot(queue, live, context);
                    Check(accepted == removed + cleared + live.Count, context + " conservation");
                }
            }
        }
    }

    [Test]
    public void Q2GeneratedCapacityBoundariesRejectWithoutChangeAndRefill()
    {
        foreach (int capacity in new[] { 1, 2, 3, 4, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65 })
        {
            foreach (int stripes in new[] { 1, 4 })
            {
                foreach (int buffer in new[] { 0, 1, 16 })
                {
                    var queue = Create(capacity, stripes, buffer);
                    var live = new Dictionary<Item, int>();
                    int id = 0;
                    for (int cycle = 0; cycle < 3; cycle++)
                    {
                        string context = $"Q2 capacity={capacity} stripes={stripes} buffer={buffer} cycle={cycle}";
                        while (live.Count < capacity)
                        {
                            var item = new Item(id++, id % 2);
                            Check(queue.TryEnqueue(item, id % 3), context + " refill");
                            live.Add(item, id % 3);
                        }

                        Check(!queue.TryEnqueue(new Item(-1, 0), int.MinValue), context + " rejection");
                        Snapshot(queue, live, context);
                        Check(queue.TryDequeue(out var removed, out _) && live.Remove(removed), context + " free slot");
                        var replacement = new Item(id++, 0);
                        Check(queue.TryEnqueue(replacement, 0), context + " reuse slot");
                        live.Add(replacement, 0);
                        Snapshot(queue, live, context);
                        queue.Clear();
                        live.Clear();
                        Snapshot(queue, live, context + " clear");
                    }
                }
            }
        }
    }

    [Test]
    public void Q3LawfulComparerMinimaAtQuiescence()
    {
        foreach (int seed in Enumerable.Range(0, 16))
        {
            foreach (int buffer in new[] { 0, 1, 16 })
            {
                foreach (bool reverse in new[] { false, true })
                {
                    var compare = Comparer<int>.Create((x, y) => reverse ? y.CompareTo(x) : x.CompareTo(y));
                    VerifyMinimum(seed, buffer, compare, x => x);
                    VerifyMinimum(seed, buffer, Comparer<RefPriority>.Create((x, y) => compare.Compare(x.Value, y.Value)), x => new RefPriority(x));
                }
            }
        }
    }

    private static void VerifyMinimum<T>(int seed, int buffer, IComparer<T> comparer, Func<int, T> priority)
    {
        var queue = ConcurrentPriorityQueue<Item, T>.Create(new() { SubQueueCount = 4, BufferCapacity = buffer, Comparer = comparer });
        var random = new Random(seed);
        var live = new Dictionary<Item, T>();
        int[] extremes = [int.MinValue, int.MaxValue, -1, 0, 1];
        for (int id = 0; id < 80; id++)
        {
            var item = new Item(id, id % 3);
            T p = priority(extremes[random.Next(extremes.Length)]);
            queue.Enqueue(item, p);
            live.Add(item, p);
        }

        while (live.Count > 0)
        {
            string context = $"Q3 seed={seed} buffer={buffer} remaining={live.Count} type={typeof(T)}";
            T minimum = live.Values.Aggregate((x, y) => comparer.Compare(x, y) <= 0 ? x : y);
            Check(queue.TryPeekBestObserved(out var peek, out var p) && live.ContainsKey(peek) && comparer.Compare(p!, minimum) == 0, context + " peek min");
            Check(queue.TryDequeueBestObserved(out var item, out p) && live.Remove(item, out var expected) && comparer.Compare(p!, expected!) == 0 && comparer.Compare(p!, minimum) == 0, context + " removal min");
        }

        Check(!queue.TryDequeueBestObserved(out _, out _), "Q3 empty");
    }

    [Test]
    public void Q4ReachableFaultPositionsPreserveSnapshotAndRecover()
    {
        int attempted = 0, reached = 0;
        foreach (int buffer in new[] { 0, 1, 16 })
        {
            foreach (int seed in Enumerable.Range(0, 4))
            {
                foreach (int prefix in new[] { 2, 3, 16, 17, 33, 65 })
                {
                    foreach (bool push in new[] { false, true })
                    {
                        int localReached = 0;
                        for (int position = 1; position <= 1024; position++)
                        {
                            attempted++;
                            var fault = new FaultComparer();
                            var queue = Create(96, 1, buffer, fault);
                            var live = new Dictionary<Item, int>();
                            var random = new Random(seed);
                            for (int id = 0; id < prefix; id++)
                            {
                                var item = new Item(id, id % 3);
                                int p = random.Next(-100, 101);
                                queue.Enqueue(item, p);
                                live.Add(item, p);
                            }

                            Snapshot(queue, live, $"Q4 before seed={seed} buffer={buffer} prefix={prefix}");
                            fault.Calls = 0;
                            fault.ThrowAt = position;
                            bool threw = false;
                            try
                            {
                                if (push) queue.TryEnqueue(new Item(1000, 0), -101);
                                else queue.TryDequeue(out _, out _);
                            }
                            catch (Fault)
                            {
                                threw = true;
                            }

                            fault.ThrowAt = int.MaxValue;
                            if (!threw) break;
                            reached++;
                            localReached++;
                            string context = $"Q4 seed={seed} buffer={buffer} prefix={prefix} push={push} position={position}";
                            Snapshot(queue, live, context);
                            while (live.Count > 0)
                            {
                                int minimum = live.Values.Min();
                                Check(queue.TryDequeueBestObserved(out var item, out int p) && live.Remove(item, out int expected) && p == expected && p == minimum, context + " recovery drain");
                            }

                            for (int id = 0; id < 96; id++) Check(queue.TryEnqueue(new Item(id, 0), id), context + " recovery capacity");
                            Check(!queue.TryEnqueue(new Item(999, 0), 0), context + " recovery rejection");
                            queue.Clear();
                            Check(queue.Count == 0 && queue.TryEnqueue(new Item(999, 0), 0), context + " recovery clear");
                        }

                        Check(localReached > 0, $"Q4 unreachable case buffer={buffer} prefix={prefix} push={push}");
                    }
                }
            }
        }

        Console.WriteLine($"Q4 attempted={attempted} reached={reached}");
    }

    [Test]
    public async Task Q5BarrierHistoriesConserveSuccessfulEffects()
    {
        foreach (int seed in Enumerable.Range(0, 16))
        {
            foreach (int buffer in new[] { 0, 1, 16 })
            {
                var queue = Create(17, 4, buffer);
                var accepted = new ConcurrentDictionary<Item, int>();
                var removed = new ConcurrentBag<(Item Element, int Priority)>();
                using var barrier = new Barrier(3);
                var actors = Enumerable.Range(0, 3).Select(actor => Task.Factory.StartNew(() =>
                {
                    var random = new Random(seed * 31 + actor);
                    for (int phase = 0; phase < 40; phase++)
                    {
                        Check(barrier.SignalAndWait(TimeSpan.FromSeconds(10)), "Q5 start timeout");
                        if ((phase + actor) % 3 != 0)
                        {
                            var item = new Item(actor * 1000 + phase, phase % 2);
                            int p = random.Next(3);
                            if (queue.TryEnqueue(item, p)) Check(accepted.TryAdd(item, p), "Q5 duplicate offer identity");
                        }
                        else if (queue.TryDequeue(out var item, out int p))
                        {
                            removed.Add((item, p));
                        }

                        Check(barrier.SignalAndWait(TimeSpan.FromSeconds(10)), "Q5 end timeout");
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
                await Task.WhenAll(actors).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                var remaining = accepted.ToDictionary();
                foreach (var entry in removed) Check(remaining.Remove(entry.Element, out int p) && p == entry.Priority, $"Q5 seed={seed} buffer={buffer} duplicate/invented removal");
                Snapshot(queue, remaining, $"Q5 seed={seed} buffer={buffer} final");
                while (queue.TryDequeue(out var item, out int p)) Check(remaining.Remove(item, out int expected) && p == expected, "Q5 final drain");
                Check(remaining.Count == 0, "Q5 final conservation");
            }
        }
    }

    [Test]
    public async Task Q5ControlledActorSchedulesHaveNoInventedEffects()
    {
        int[][] schedules = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
        foreach (int[] schedule in schedules)
        {
            foreach (int buffer in new[] { 0, 1, 16 })
            {
                var queue = Create(3, 2, buffer);
                var live = new Dictionary<Item, int>();
                var gates = Enumerable.Range(0, 3).Select(_ => new SemaphoreSlim(0)).ToArray();
                using var completed = new SemaphoreSlim(0);
                var actors = Enumerable.Range(0, 3).Select(actor => Task.Run(async () =>
                {
                    for (int round = 0; round < 12; round++)
                    {
                        Check(await gates[actor].WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false), "Q5 actor gate timeout");
                        string context = $"Q5 schedule={string.Join(',', schedule)} buffer={buffer} round={round} actor={actor}";
                        if ((round + actor) % 3 != 0)
                        {
                            var item = new Item(actor * 100 + round, round % 2);
                            bool accepted = queue.TryEnqueue(item, round % 3);
                            Check(accepted == (live.Count < 3), context + " admission");
                            if (accepted) live.Add(item, round % 3);
                        }
                        else if (queue.TryDequeue(out var item, out int priority))
                        {
                            Check(live.Remove(item, out int expected) && priority == expected, context + " removal");
                        }
                        else
                        {
                            Check(live.Count == 0, context + " observed empty without overlap");
                        }

                        Snapshot(queue, live, context);
                        completed.Release();
                    }
                })).ToArray();
                try
                {
                    for (int round = 0; round < 12; round++)
                    {
                        foreach (int actor in schedule)
                        {
                            gates[actor].Release();
                            bool signaled = await completed.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                            if (!signaled)
                            {
                                if (actors[actor].IsFaulted) await actors[actor].ConfigureAwait(false);
                                throw new TimeoutException("Q5 coordinator timeout");
                            }
                        }
                    }

                    await Task.WhenAll(actors).ConfigureAwait(false);
                    Snapshot(queue, live, "Q5 controlled final");
                }
                finally
                {
                    foreach (var gate in gates) gate.Dispose();
                }
            }
        }
    }

    [Test]
    public void MetamorphicQuiescentRelations()
    {
        foreach (int seed in Enumerable.Range(0, 32))
        {
            var random = new Random(seed);
            var input = Enumerable.Range(0, 96).Select(id => (Element: new Item(id, random.Next(3)), Priority: random.Next(-1000, 1001))).ToArray();
            var baseline = Drain(input, 1, 0);
            foreach (int stripes in new[] { 1, 2, 4 })
            {
                foreach (int buffer in new[] { 0, 1, 16 })
                {
                    var actual = Drain(input, stripes, buffer);
                    Check(baseline.SequenceEqual(actual), $"M configuration seed={seed}");
                    var translated = Drain(input.Select(x => (x.Element, x.Priority + 1234)).ToArray(), stripes, buffer).Select(x => (x.Element, Priority: x.Priority - 1234));
                    Check(baseline.SequenceEqual(translated), $"M translation seed={seed}");
                    var bijection = Drain(input.Select(x => (new Item(x.Element.Id + 10000, x.Element.Value + 7), x.Priority)).ToArray(), stripes, buffer).Select(x => (new Item(x.Element.Id - 10000, x.Element.Value - 7), x.Priority));
                    Check(baseline.SequenceEqual(bijection), $"M payload seed={seed}");
                    var doubled = Drain(input.Concat(input).ToArray(), stripes, buffer);
                    Check(doubled.SequenceEqual(baseline.SelectMany(x => new[] { x, x })), $"M multiplicity seed={seed}");
                    Check(baseline.SequenceEqual(Drain(input, stripes, buffer, true)), $"M clear-prefix seed={seed}");
                }
            }
        }
    }

    private static (Item Element, int Priority)[] Drain((Item Element, int Priority)[] input, int stripes, int buffer, bool prefix = false)
    {
        var queue = ConcurrentPriorityQueue<Item, int>.Create(new() { SubQueueCount = stripes, BufferCapacity = buffer });
        if (prefix)
        {
            foreach (var item in input.Take(31)) queue.Enqueue(new Item(-item.Element.Id - 1, 9), int.MinValue);
            queue.Clear();
        }

        foreach (var item in input) queue.Enqueue(item.Element, item.Priority);
        var output = new List<(Item Element, int Priority)>();
        int previous = int.MinValue;
        while (queue.TryDequeueBestObserved(out var item, out int p))
        {
            Check(p >= previous, "M quiescent priority order");
            previous = p;
            output.Add((item, p));
        }

        Check(output.Count == input.Length, "M output count");
        return output.OrderBy(x => x.Priority).ThenBy(x => x.Element.Id).ThenBy(x => x.Element.Value).ToArray();
    }
}