using Bifrost.Concurrency;

namespace Bifrost.Tests.Concurrency;

internal class ComparerClearRefillRegressionTests
{
    [Test]
    public void FailedEnqueueThenClearMatchesFreshQueueCapacity()
    {
        var comparer = new FaultComparer();
        var queue = ConcurrentPriorityQueue<string, int>.Create(new()
        {
            Capacity = 8, Stickiness = int.MaxValue, BufferCapacity = 0, Comparer = comparer,
        });
        var fresh = ConcurrentPriorityQueue<string, int>.Create(new()
        {
            Capacity = 8, Stickiness = int.MaxValue, BufferCapacity = 0, Comparer = comparer,
        });
        queue.Enqueue("retained", 10);
        comparer.Fail = true;
        bool threw = false;
        try
        {
            queue.Enqueue("failed", 5);
        }
        catch (ComparisonFailure)
        {
            threw = true;
        }
        finally
        {
            comparer.Fail = false;
        }

        Require(threw, "fault injection reached the comparer");
        var snapshot = queue.ToArray();
        Require(queue.Count == 1 && snapshot.Length == 1 && snapshot[0] == ("retained", 10),
            "failed enqueue preserves count and exact membership");
        queue.Clear();
        Require(queue.Count == 0 && queue.ToArray().Length == 0, "clear removes the retained item");
        for (int i = 0; i < 10; i++)
        {
            bool accepted = queue.TryEnqueue($"new{i}", i);
            bool control = fresh.TryEnqueue($"new{i}", i);
            Require(accepted == control && accepted == (i < 8), "clear/refill admits exactly the fresh-queue suffix");
            Require(queue.Count == Math.Min(i + 1, 8), "count never exceeds capacity");
        }

        Require(queue.ToArray().OrderBy(x => x.Priority).SequenceEqual(fresh.ToArray().OrderBy(x => x.Priority)),
            "refill retains the same identities as a fresh queue");
    }

    private static void Require(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
    }

    private sealed class ComparisonFailure : Exception;

    private sealed class FaultComparer : IComparer<int>
    {
        internal bool Fail;
        public int Compare(int x, int y)
        {
            if (Fail) throw new ComparisonFailure();
            return x.CompareTo(y);
        }
    }
}
