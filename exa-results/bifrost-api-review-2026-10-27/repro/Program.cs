using Bifrost.Concurrency;

// Deterministic fault schedules using the library's existing internal test hooks.
// These compile the unchanged queue source directly, without NuGet dependencies.
var unknown = new ConcurrentPriorityQueue<int, int>(subQueueCount: 2, boundedCapacity: -1, comparer: null);
unknown.SubQueuesForTest[0].TryLockedPush(1, 1);
unknown.SubQueuesForTest[1].TryLockedPush(10, 10);
unknown.SubQueuesForTest[0].DebugForceOddVersionForTest();
bool selected = unknown.TryDequeueMin(out _, out int priority);
Console.WriteLine($"Unknown minimum stripe: success={selected}, returned={priority}, minimum 1 still resident={unknown.SubQueuesForTest[0].VolatileCount == 1}");
if (!selected || priority != 10) throw new Exception("Unexpected schedule result");

var locked = new ConcurrentPriorityQueue<int, int>(subQueueCount: 1, boundedCapacity: -1, comparer: null);
locked.Enqueue(7, 7);
using var held = new ManualResetEventSlim();
using var release = new ManualResetEventSlim();
var holder = new Thread(() =>
{
    locked.SubQueuesForTest[0].SyncLock.Enter();
    try { held.Set(); release.Wait(); }
    finally { locked.SubQueuesForTest[0].SyncLock.Exit(); }
});
holder.Start();
held.Wait();
try
{
    bool peek = locked.TryPeek(out _, out _);
    bool dequeue = locked.TryDequeueMin(out _, out _);
    Console.WriteLine($"Held populated stripe: Count={locked.Count}, TryPeek={peek}, TryDequeueMin={dequeue}");
    if (peek || dequeue || locked.Count != 1) throw new Exception("Unexpected contention result");
}
finally { release.Set(); holder.Join(); }

var comparer = new ThrowingComparer();
var failed = new ConcurrentPriorityQueue<int, int>(subQueueCount: 1, boundedCapacity: 2, comparer: comparer);
failed.Enqueue(1, 1);
comparer.Throws = true;
try { failed.Enqueue(2, 2); }
catch (InvalidOperationException) { }
comparer.Throws = false;
Console.WriteLine($"After comparer failure: Count={failed.Count}, stored entries={failed.ToArray().Length}, reservations={failed.DebugBoundedCountForTest}");
failed.Clear();
Console.WriteLine($"After Clear: reservations={failed.DebugBoundedCountForTest}");
int accepted = 0;
for (int i = 0; i < 4; i++) if (failed.TryEnqueue(i, i)) accepted++;
Console.WriteLine($"After Clear and refill: accepted={accepted}, Count={failed.Count}, BoundedCapacity={failed.BoundedCapacity}");
if (accepted != 3 || failed.Count != 3) throw new Exception("Unexpected recovery result");

sealed class ThrowingComparer : IComparer<int>
{
    public bool Throws;
    public int Compare(int x, int y) => Throws ? throw new InvalidOperationException("comparison failed") : x.CompareTo(y);
}
