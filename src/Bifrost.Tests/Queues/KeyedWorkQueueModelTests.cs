// =============================================================================
// <copyright file="KeyedWorkQueueModelTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Bifrost.Core;
using Bifrost.Queues;
using Bifrost.Tests.Systematic;

namespace Bifrost.Tests.Queues;

/// <summary>
/// Seeded model test for <see cref="KeyedWorkQueue{TWork, TKey}"/>: random single-threaded
/// operation sequences run against the binding and against a simple reference model, and every
/// observable result and counter must match after each step.
/// </summary>
/// <remarks>
/// The reference model keeps a plain list per key, an active count per key, a ready list of keys,
/// and a permit counter. It funds a permit whenever the number of dispatchable items grows, which
/// is the contract under test. A failure message names the seed and step, so a run can be replayed.
/// Set <c>BIFROST_INVARIANT_SEED_MULTIPLIER</c> to run more seeds.
/// </remarks>
public sealed class KeyedWorkQueueModelTests
{
    private const int Steps = 400;
    private const int KeyCount = 4;

    /// <summary>Random operation sequences match the reference model at every step.</summary>
    /// <param name="maxConcurrencyPerKey">The per-key concurrency under test.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task RandomOperations_MatchReferenceModel(int maxConcurrencyPerKey)
    {
        var seeds = InvariantRun.SeedCount(40);
        for (var seed = 0; seed < seeds; seed++)
        {
            var failure = Run(seed, maxConcurrencyPerKey);
            await Assert.That(failure).IsNull();
        }
    }

    private static string? Run(int seed, int maxConcurrencyPerKey)
    {
        var random = new Random(seed);
        var capacity = random.Next(1, 9);
        var maxPendingPerKey = random.Next(1, 5);
        using var queue = KeyedWorkQueue.Create<Item, int>(static item => item.Key, capacity, maxPendingPerKey, maxConcurrencyPerKey);
        var model = new Model(capacity, maxPendingPerKey, maxConcurrencyPerKey);
        var leases = new List<(object Lease, int Key)>();
        var sequence = 0;

        for (var step = 0; step < Steps; step++)
        {
            var where = $"seed {seed}, step {step}, cap {maxConcurrencyPerKey}, capacity {capacity}, pending {maxPendingPerKey}";
            var op = random.Next(100);
            if (op < 40)
            {
                var item = new Item(random.Next(KeyCount), sequence++);
                var actual = queue.Enqueue(new WorkEnvelope<Item>(item, WorkClass.Default, 0));
                var expected = model.Enqueue(item);
                if (actual != expected) return $"{where}: enqueue {item} returned {actual}, model {expected}";
            }
            else if (op < 60)
            {
                if (model.CompletedAndEmpty)
                {
                    if (queue.WaitToDequeueAsync(CancellationToken.None).AsTask() is not { IsCompletedSuccessfully: true, Result: false })
                        return $"{where}: wait on a completed, empty queue did not return false";
                }
                else if (model.Permits > 0)
                {
                    if (queue.WaitToDequeueAsync(CancellationToken.None).AsTask() is not { IsCompletedSuccessfully: true, Result: true })
                        return $"{where}: a funded wait did not return true at once";
                    model.Permits--;
                    var failure = Dequeue(queue, model, leases, where, matched: false);
                    if (failure is not null) return failure;
                }
            }
            else if (op < 75)
            {
                var failure = Dequeue(queue, model, leases, where, matched: true);
                if (failure is not null) return failure;
            }
            else if (op < 95)
            {
                if (leases.Count > 0)
                {
                    var index = random.Next(leases.Count);
                    var (lease, key) = leases[index];
                    leases.RemoveAt(index);
                    queue.Release(lease);
                    model.Release(key);
                }
            }
            else if (op < 98)
            {
                queue.WakeConsumer();
                model.Permits++;
            }
            else
            {
                queue.Complete();
                model.Completed = true;
            }

            var mismatch = Compare(queue, model, where);
            if (mismatch is not null) return mismatch;
        }

        foreach (var (lease, key) in leases)
        {
            queue.Release(lease);
            model.Release(key);
        }

        return Compare(queue, model, $"seed {seed}, final release");
    }

    private static string? Dequeue(KeyedWorkQueue<Item, int> queue, Model model, List<(object Lease, int Key)> leases, string where, bool matched)
    {
        var took = queue.TryDequeue(out var envelope, out var lease);
        var expected = model.TryDequeue(out var expectedItem);
        if (took != expected) return $"{where}: dequeue returned {took}, model {expected}";
        if (!took)
        {
            if (lease is not null) return $"{where}: a dequeue miss returned a lease";
            return null;
        }

        if (envelope.Work != expectedItem) return $"{where}: dequeued {envelope.Work}, model {expectedItem}";
        if (lease is null) return $"{where}: a dequeue returned no lease";
        leases.Add((lease, envelope.Work.Key));
        if (matched)
        {
            queue.ConsumeMatchedWake();
            if (!model.CompletedAndEmpty && model.Permits > 0) model.Permits--;
        }

        return null;
    }

    private static string? Compare(KeyedWorkQueue<Item, int> queue, Model model, string where)
    {
        if (queue.Count != model.Queued) return $"{where}: count {queue.Count}, model {model.Queued}";
        if (queue.DispatchableCount != model.Dispatchable) return $"{where}: dispatchable {queue.DispatchableCount}, model {model.Dispatchable}";
        if (queue.PendingWakeCountForTest != model.Permits) return $"{where}: permits {queue.PendingWakeCountForTest}, model {model.Permits}";
        if (model.Permits < model.Dispatchable) return $"{where}: permits {model.Permits} fall short of dispatchable {model.Dispatchable}";
        if (queue.TrackedKeyCountForTest != model.TrackedKeys) return $"{where}: tracked keys {queue.TrackedKeyCountForTest}, model {model.TrackedKeys}";
        if (queue.ReadyKeyCountForTest != model.ReadyKeys) return $"{where}: ready keys {queue.ReadyKeyCountForTest}, model {model.ReadyKeys}";
        for (var key = 0; key < KeyCount; key++)
        {
            if (queue.ActiveCountForTest(key) != model.Active[key]) return $"{where}: key {key} active {queue.ActiveCountForTest(key)}, model {model.Active[key]}";
        }

        return null;
    }

    private readonly record struct Item(int Key, int Sequence);

    private sealed class Model(int capacity, int maxPendingPerKey, int maxConcurrencyPerKey)
    {
        private readonly List<Item>[] _items = Enumerable.Range(0, KeyCount).Select(static _ => new List<Item>()).ToArray();
        private readonly List<int> _ready = [];

        public int[] Active { get; } = new int[KeyCount];

        public int Permits { get; set; }

        public bool Completed { get; set; }

        public int Queued => _items.Sum(static list => list.Count);

        public bool CompletedAndEmpty => Completed && Queued == 0;

        public int Dispatchable => Enumerable.Range(0, KeyCount).Sum(k => Math.Min(_items[k].Count, maxConcurrencyPerKey - Active[k]));

        public int TrackedKeys => Enumerable.Range(0, KeyCount).Count(k => _items[k].Count > 0 || Active[k] > 0);

        public int ReadyKeys => _ready.Count;

        public EnqueueResult Enqueue(Item item)
        {
            if (Completed) return EnqueueResult.Rejected(RejectionReason.Shutdown);
            if (Queued >= capacity) return EnqueueResult.Rejected(RejectionReason.CapacityExceeded);
            if (_items[item.Key].Count >= maxPendingPerKey) return EnqueueResult.Rejected(RejectionReason.KeyLimitExceeded);
            var before = Dispatchable;
            _items[item.Key].Add(item);
            Readify(item.Key);
            Permits += Dispatchable - before;
            return EnqueueResult.Accepted;
        }

        public bool TryDequeue(out Item item)
        {
            if (_ready.Count == 0)
            {
                item = default;
                return false;
            }

            var key = _ready[0];
            _ready.RemoveAt(0);
            item = _items[key][0];
            _items[key].RemoveAt(0);
            Active[key]++;
            Readify(key);
            return true;
        }

        public void Release(int key)
        {
            var before = Dispatchable;
            Active[key]--;
            Readify(key);
            Permits += Dispatchable - before;
        }

        private void Readify(int key)
        {
            if (_items[key].Count > 0 && Active[key] < maxConcurrencyPerKey && !_ready.Contains(key)) _ready.Add(key);
        }
    }
}
