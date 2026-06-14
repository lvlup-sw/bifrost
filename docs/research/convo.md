Abandoning the strict global count is definitely the right call. It protects the exact scalability you are paying the "relaxation cost" to achieve.

Here is how the **transition bitmask** elegantly solves both the contention problem and the linearizability (false emptiness) trap, while throwing in a massive performance bonus for the sparse regime.

### 1. The Structure of the Bitmask

For your 64-core machine running 256 sub-queues, a bitmask is incredibly compact. You only need 256 bits of data, which fits perfectly into an array of exactly **four 64-bit integers (`ulong`)**.

Each bit represents the state of one specific sub-queue:

- `0` = Empty
- `1` = Contains at least one item

### 2. Solving Contention (The "Transition" Rule)

The golden rule of the transition bitmask is that a thread *only* writes to it when a sub-queue crosses the emptiness boundary.

- **Enqueueing:** When a producer locks a sub-queue to insert an item, it checks the local count. If the count is `0`, it uses a non-blocking `Interlocked.Or` to flip that specific sub-queue's bit to `1`. If the count is already > `0`, it does absolutely nothing to the bitmask.
- **Dequeueing:** When a consumer extracts an item, if the local count drops to `0`, it uses an `Interlocked.And` to flip the bit back to `0`. If there are still items left, it ignores the bitmask.

**Why it scales:** During your benchmark's peak of 114 M ops/s, the sub-queues are constantly hovering between hundreds or thousands of items. Because they never hit `0`, the bitmask is literally **never written to**. It sits perfectly dormant in the CPU cache, causing zero contention.

### 3. Solving the Linearizability Trap (Honest Emptiness)

To fix the race condition where a consumer mistakenly thinks the system is empty while a producer is actively inserting an item, you rely on strict memory ordering inside the producer's lock.

1. The producer acquires the lock for Sub-queue $X$.
2. It sees the queue is empty.
3. **Crucial Step:** It sets Bit $X$ to `1` in the global bitmask *before* or *during* the insertion, while still holding the lock.
4. It inserts the item and releases the lock.

Because the bit is flipped while the lock is held, it is impossible for a consumer to observe a "false empty." If a consumer reads the bitmask and sees all 256 bits are `0`, it mathematically guarantees that at that exact nanosecond, no sub-queue contains an item, and no producer is currently in the middle of inserting a first item.

### 4. The Sparse Regime Bonus: Instant Routing

This is where the bitmask goes from being a good idea to a brilliantly optimized one.

When the queue drains and enters the "sparse regime," your benchmark notes that random sampling fails 96% of the time, forcing an expensive $O(n)$ scan. The bitmask completely eliminates this.

If a consumer's random sample comes up empty, instead of scanning 256 locks, it just reads the four `ulong` bitmasks.

- If the bitmask is `0`, it returns `null` immediately. Honest emptiness achieved in $O(1)$.
- If the bitmask is `> 0`, the consumer knows *exactly* which sub-queues have items. It can use a fast hardware intrinsic like `BitOperations.TrailingZeroCount` to instantly find the index of a non-empty sub-queue and route directly to it. The sparse penalty vanishes.

Looking at your benchmark environment, you are running on a Xeon Platinum with AVX-512 support. Since 256 bits fits perfectly into a single `Vector256` SIMD register, are you planning to vectorize the emptiness check so a thread can read the entire state of the MultiQueue in a single CPU instruction?