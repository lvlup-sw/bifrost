# Bifrost CPQ Buffered-vs-Unbuffered Throughput A/B

```text
Bifrost CPQ throughput sweep — generated 2026-06-15 19:25:20Z
ProcessorCount: 32
OS: Pop!_OS 24.04 LTS
CPU arch: X64
Runtime: .NET 10.0.6
GC mode: Workstation, Concurrent: Interactive
```

| Target | Workload | Threads | Buffer C | Stickiness | Window (s) | Total Ops | Ops/sec |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| MultiQueueRelaxed | UniformMixed5050 | 1 | 0 | 1 | 1 | 17,086,016 | 17,083,546 |
| MultiQueueRelaxed | UniformMixed5050 | 1 | 16 | 1 | 1 | 21,650,670 | 21,649,434 |
| MultiQueueRelaxed | UniformMixed5050 | 2 | 0 | 1 | 1 | 22,048,346 | 22,046,190 |
| MultiQueueRelaxed | UniformMixed5050 | 2 | 16 | 1 | 1 | 24,269,438 | 24,266,742 |
| MultiQueueRelaxed | UniformMixed5050 | 4 | 0 | 1 | 1 | 33,514,826 | 33,512,715 |
| MultiQueueRelaxed | UniformMixed5050 | 4 | 16 | 1 | 1 | 37,531,760 | 37,527,797 |
| MultiQueueRelaxed | UniformMixed5050 | 8 | 0 | 1 | 1 | 54,525,584 | 54,522,334 |
| MultiQueueRelaxed | UniformMixed5050 | 8 | 16 | 1 | 1 | 63,938,168 | 63,934,236 |
| MultiQueueRelaxed | UniformMixed5050 | 16 | 0 | 1 | 1 | 76,986,230 | 76,981,403 |
| MultiQueueRelaxed | UniformMixed5050 | 16 | 16 | 1 | 1 | 94,049,728 | 94,044,161 |
| MultiQueueRelaxed | UniformMixed5050 | 32 | 0 | 1 | 1 | 83,583,554 | 83,504,367 |
| MultiQueueRelaxed | UniformMixed5050 | 32 | 16 | 1 | 1 | 131,989,486 | 131,981,699 |
| MultiQueueRelaxed | UniformMixed5050 | 64 | 0 | 1 | 1 | 80,717,042 | 80,453,733 |
| MultiQueueRelaxed | UniformMixed5050 | 64 | 16 | 1 | 1 | 117,236,688 | 117,145,490 |
| MultiQueueRelaxed | SplitProducerConsumer | 1 | 0 | 1 | 1 | 22,964,459 | 22,963,028 |
| MultiQueueRelaxed | SplitProducerConsumer | 1 | 16 | 1 | 1 | 23,705,253 | 23,703,826 |
| MultiQueueRelaxed | SplitProducerConsumer | 2 | 0 | 1 | 1 | 21,023,591 | 21,021,441 |
| MultiQueueRelaxed | SplitProducerConsumer | 2 | 16 | 1 | 1 | 22,106,433 | 22,105,056 |
| MultiQueueRelaxed | SplitProducerConsumer | 4 | 0 | 1 | 1 | 32,196,563 | 32,194,345 |
| MultiQueueRelaxed | SplitProducerConsumer | 4 | 16 | 1 | 1 | 35,614,408 | 35,612,339 |
| MultiQueueRelaxed | SplitProducerConsumer | 8 | 0 | 1 | 1 | 47,824,519 | 47,821,525 |
| MultiQueueRelaxed | SplitProducerConsumer | 8 | 16 | 1 | 1 | 59,999,697 | 59,995,839 |
| MultiQueueRelaxed | SplitProducerConsumer | 16 | 0 | 1 | 1 | 75,153,706 | 75,148,949 |
| MultiQueueRelaxed | SplitProducerConsumer | 16 | 16 | 1 | 1 | 87,464,915 | 87,459,405 |
| MultiQueueRelaxed | SplitProducerConsumer | 32 | 0 | 1 | 1 | 88,483,362 | 88,478,124 |
| MultiQueueRelaxed | SplitProducerConsumer | 32 | 16 | 1 | 1 | 116,635,701 | 116,548,057 |
| MultiQueueRelaxed | SplitProducerConsumer | 64 | 0 | 1 | 1 | 79,938,079 | 79,815,180 |
| MultiQueueRelaxed | SplitProducerConsumer | 64 | 16 | 1 | 1 | 108,629,326 | 108,397,853 |
| MultiQueueRelaxed | NarrowKeyRange | 1 | 0 | 1 | 1 | 23,856,024 | 23,854,621 |
| MultiQueueRelaxed | NarrowKeyRange | 1 | 16 | 1 | 1 | 29,100,676 | 29,098,916 |
| MultiQueueRelaxed | NarrowKeyRange | 2 | 0 | 1 | 1 | 24,363,198 | 24,361,595 |
| MultiQueueRelaxed | NarrowKeyRange | 2 | 16 | 1 | 1 | 26,088,528 | 26,085,768 |
| MultiQueueRelaxed | NarrowKeyRange | 4 | 0 | 1 | 1 | 36,302,136 | 36,299,983 |
| MultiQueueRelaxed | NarrowKeyRange | 4 | 16 | 1 | 1 | 38,819,290 | 38,816,604 |
| MultiQueueRelaxed | NarrowKeyRange | 8 | 0 | 1 | 1 | 56,763,460 | 56,759,947 |
| MultiQueueRelaxed | NarrowKeyRange | 8 | 16 | 1 | 1 | 66,345,804 | 66,341,870 |
| MultiQueueRelaxed | NarrowKeyRange | 16 | 0 | 1 | 1 | 77,836,872 | 77,832,334 |
| MultiQueueRelaxed | NarrowKeyRange | 16 | 16 | 1 | 1 | 95,811,384 | 95,805,473 |
| MultiQueueRelaxed | NarrowKeyRange | 32 | 0 | 1 | 1 | 81,267,136 | 81,203,797 |
| MultiQueueRelaxed | NarrowKeyRange | 32 | 16 | 1 | 1 | 131,525,202 | 131,438,439 |
| MultiQueueRelaxed | NarrowKeyRange | 64 | 0 | 1 | 1 | 78,939,264 | 78,758,835 |
| MultiQueueRelaxed | NarrowKeyRange | 64 | 16 | 1 | 1 | 118,295,616 | 118,026,162 |
| MultiQueueRelaxed | Drain | 1 | 0 | 1 | 1 | 1,000,000 | 13,974,912 |
| MultiQueueRelaxed | Drain | 1 | 16 | 1 | 1 | 1,000,000 | 15,460,108 |
| MultiQueueRelaxed | Drain | 2 | 0 | 1 | 1 | 1,000,000 | 16,304,623 |
| MultiQueueRelaxed | Drain | 2 | 16 | 1 | 1 | 1,000,000 | 18,041,033 |
| MultiQueueRelaxed | Drain | 4 | 0 | 1 | 1 | 1,000,000 | 24,930,693 |
| MultiQueueRelaxed | Drain | 4 | 16 | 1 | 1 | 1,000,000 | 27,382,181 |
| MultiQueueRelaxed | Drain | 8 | 0 | 1 | 1 | 1,000,000 | 36,757,544 |
| MultiQueueRelaxed | Drain | 8 | 16 | 1 | 1 | 1,000,000 | 40,553,474 |
| MultiQueueRelaxed | Drain | 16 | 0 | 1 | 1 | 1,000,000 | 52,410,627 |
| MultiQueueRelaxed | Drain | 16 | 16 | 1 | 1 | 1,000,000 | 55,678,047 |
| MultiQueueRelaxed | Drain | 32 | 0 | 1 | 1 | 1,000,000 | 49,621,635 |
| MultiQueueRelaxed | Drain | 32 | 16 | 1 | 1 | 1,000,000 | 72,019,128 |
| MultiQueueRelaxed | Drain | 64 | 0 | 1 | 1 | 1,000,000 | 39,626,402 |
| MultiQueueRelaxed | Drain | 64 | 16 | 1 | 1 | 1,000,000 | 72,647,493 |
