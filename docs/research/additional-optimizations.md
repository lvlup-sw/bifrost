Here are the three most critical, hardware-agnostic optimizations you should apply to this data structure to squeeze out maximum throughput.

### 1. Cache Line Padding (Defeating False Sharing)

This is the silent killer of array-backed concurrent structures. If you miss this, your 64-core scalability will vanish.

- **The Problem:** Modern CPUs fetch memory in 64-byte or 128-byte chunks called cache lines. If your `SubQueue` struct/class (which contains the lock, the array pointer, and the count) is only 24 bytes, the CPU will pack 2 or 3 sub-queues into a single 64-byte cache line.
- **The Contention:** If Thread A randomly selects Sub-queue 0, and Thread B randomly selects Sub-queue 1, they are technically acquiring entirely different software locks. However, because both locks live on the exact same physical cache line, the CPU hardware will lock the cache line, forcing the cores to serialize. This is called **false sharing**, and it completely undermines the "sharded" nature of the MultiQueue.
- **The Fix:** You must explicitly pad your sub-queue objects so that each one begins on a fresh cache line. In .NET, you can force this using `[StructLayout(LayoutKind.Explicit, Size = 64)]` (or 128 for aggressive prefetching architectures) to guarantee that no two sub-queue locks ever share the same physical silicon pathway.

### 2. Thread-Local, Non-Cryptographic RNG

The MultiQueue lives and dies by the speed of its random number generator. At your benchmarked 114 M ops/s, your system is generating at least 228 million random numbers per second just to execute the Power of Two Choices.

- **The Problem:** If you use a standard `System.Random` (which may have internal synchronization) or a cryptographically secure RNG, the generation overhead will eclipse the actual lock acquisition. Even worse, if multiple threads share an RNG instance, they will contend on the RNG's internal seed state.
- **The Fix:** Every consumer thread must have its own isolated, thread-local RNG. Furthermore, it should use an extremely fast, non-cryptographic algorithm like **XorShift** or **PCG (Permuted Congruential Generator)**. These algorithms require only two or three basic bitwise operations (shifts and XORs) per generation, keeping the random sampling overhead to single-digit nanoseconds.

### 3. $d$-ary Heaps for Sub-queues

Since your sub-queues are isolated and array-backed, you have the opportunity to optimize their internal sorting mechanics. The default for priority queues is a binary heap, but this is sub-optimal for modern caches.

- **The Problem:** In a standard binary heap, a parent at index $i$ has children at $2i$ and $2i+1$. As the heap gets deeper, traversing down the tree causes the CPU to jump wildly across the array, causing L1 cache misses.
- **The Fix:** Swap the binary heaps inside your sub-queues for **$d$-ary heaps** (typically 4-ary or 8-ary). In a 4-ary heap, each parent has 4 children. This flattens the tree considerably (reducing the total number of swaps required to bubble an item down). More importantly, the 4 children are stored contiguously in the array, meaning a single cache line fetch pulls all of a node's children into the L1 cache simultaneously. The thread can vectorize or unroll the comparison of all 4 children almost instantly.

**4. Vectorization**

See https://github.com/lvlup-sw/DataFerry/blob/r%26d/src/DataFerry/Collections/CountMinSketch.cs

hardware dependent