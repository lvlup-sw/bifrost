# Bifrost CPQ Throughput Sweep

```text
Bifrost CPQ throughput sweep — generated 2026-06-15 21:45:55Z
ProcessorCount: 64
OS: Ubuntu 24.04.4 LTS
CPU arch: X64
Runtime: .NET 10.0.9
GC mode: Workstation, Concurrent: Interactive
```

| Target | Workload | Threads | Stickiness | Window (s) | Total Ops | Ops/sec |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| MultiQueueRelaxed | UniformMixed5050 | 1 | 1 | 3 | 37,148,442 | 12,382,268 |
| MultiQueueRelaxed | UniformMixed5050 | 1 | 2 | 3 | 39,779,614 | 13,259,498 |
| MultiQueueRelaxed | UniformMixed5050 | 1 | 4 | 3 | 41,653,072 | 13,883,880 |
| MultiQueueRelaxed | UniformMixed5050 | 1 | 8 | 3 | 41,992,302 | 13,996,996 |
| MultiQueueRelaxed | UniformMixed5050 | 2 | 1 | 3 | 31,881,138 | 10,626,741 |
| MultiQueueRelaxed | UniformMixed5050 | 2 | 2 | 3 | 41,126,956 | 13,708,591 |
| MultiQueueRelaxed | UniformMixed5050 | 2 | 4 | 3 | 51,185,068 | 17,061,348 |
| MultiQueueRelaxed | UniformMixed5050 | 2 | 8 | 3 | 60,452,042 | 20,150,124 |
| MultiQueueRelaxed | SplitProducerConsumer | 1 | 1 | 3 | 35,745,273 | 11,914,763 |
| MultiQueueRelaxed | SplitProducerConsumer | 1 | 2 | 3 | 35,121,416 | 11,706,750 |
| MultiQueueRelaxed | SplitProducerConsumer | 1 | 4 | 3 | 37,390,849 | 12,463,260 |
| MultiQueueRelaxed | SplitProducerConsumer | 1 | 8 | 3 | 36,430,640 | 12,143,186 |
| MultiQueueRelaxed | SplitProducerConsumer | 2 | 1 | 3 | 30,592,391 | 10,197,107 |
| MultiQueueRelaxed | SplitProducerConsumer | 2 | 2 | 3 | 36,456,164 | 12,151,776 |
| MultiQueueRelaxed | SplitProducerConsumer | 2 | 4 | 3 | 44,767,280 | 14,921,401 |
| MultiQueueRelaxed | SplitProducerConsumer | 2 | 8 | 3 | 53,663,270 | 17,887,215 |
| MultiQueueRelaxed | NarrowKeyRange | 1 | 1 | 3 | 44,690,634 | 14,896,360 |
| MultiQueueRelaxed | NarrowKeyRange | 1 | 2 | 3 | 45,161,830 | 15,053,142 |
| MultiQueueRelaxed | NarrowKeyRange | 1 | 4 | 3 | 47,120,100 | 15,706,193 |
| MultiQueueRelaxed | NarrowKeyRange | 1 | 8 | 3 | 47,079,650 | 15,692,806 |
| MultiQueueRelaxed | NarrowKeyRange | 2 | 1 | 3 | 34,080,406 | 11,359,826 |
| MultiQueueRelaxed | NarrowKeyRange | 2 | 2 | 3 | 44,859,986 | 14,952,876 |
| MultiQueueRelaxed | NarrowKeyRange | 2 | 4 | 3 | 56,135,940 | 18,711,520 |
| MultiQueueRelaxed | NarrowKeyRange | 2 | 8 | 3 | 65,889,328 | 21,962,587 |
| MultiQueueRelaxed | Drain | 1 | 1 | 3 | 1,000,000 | 9,369,384 |
| MultiQueueRelaxed | Drain | 1 | 2 | 3 | 1,000,000 | 11,510,884 |
| MultiQueueRelaxed | Drain | 1 | 4 | 3 | 1,000,000 | 12,440,256 |
| MultiQueueRelaxed | Drain | 1 | 8 | 3 | 1,000,000 | 12,888,525 |
| MultiQueueRelaxed | Drain | 2 | 1 | 3 | 1,000,000 | 7,827,237 |
| MultiQueueRelaxed | Drain | 2 | 2 | 3 | 1,000,000 | 10,085,851 |
| MultiQueueRelaxed | Drain | 2 | 4 | 3 | 1,000,000 | 13,773,403 |
| MultiQueueRelaxed | Drain | 2 | 8 | 3 | 1,000,000 | 17,182,928 |
