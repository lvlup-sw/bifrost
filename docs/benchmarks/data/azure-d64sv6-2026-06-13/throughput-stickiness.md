# Bifrost CPQ Throughput Sweep

```text
Bifrost CPQ throughput sweep — generated 2026-06-13 22:56:39Z
ProcessorCount: 64
OS: Ubuntu 24.04.4 LTS
CPU arch: X64
Runtime: .NET 10.0.9
GC mode: Workstation, Concurrent: Interactive
```

| Target | Workload | Threads | Stickiness | Window (s) | Total Ops | Ops/sec |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| MultiQueueRelaxed | UniformMixed5050 | 1 | 1 | 3 | 39,608,448 | 13,202,298 |
| MultiQueueRelaxed | UniformMixed5050 | 1 | 2 | 3 | 42,203,044 | 14,067,387 |
| MultiQueueRelaxed | UniformMixed5050 | 1 | 4 | 3 | 43,969,544 | 14,656,168 |
| MultiQueueRelaxed | UniformMixed5050 | 1 | 8 | 3 | 44,748,464 | 14,915,894 |
| MultiQueueRelaxed | UniformMixed5050 | 2 | 1 | 3 | 31,430,568 | 10,476,565 |
| MultiQueueRelaxed | UniformMixed5050 | 2 | 2 | 3 | 40,595,776 | 13,531,697 |
| MultiQueueRelaxed | UniformMixed5050 | 2 | 4 | 3 | 52,936,268 | 17,645,052 |
| MultiQueueRelaxed | UniformMixed5050 | 2 | 8 | 3 | 62,364,826 | 20,787,956 |
| MultiQueueRelaxed | SplitProducerConsumer | 1 | 1 | 3 | 37,923,504 | 12,641,031 |
| MultiQueueRelaxed | SplitProducerConsumer | 1 | 2 | 3 | 37,221,186 | 12,406,763 |
| MultiQueueRelaxed | SplitProducerConsumer | 1 | 4 | 3 | 37,813,370 | 12,604,071 |
| MultiQueueRelaxed | SplitProducerConsumer | 1 | 8 | 3 | 38,965,609 | 12,988,188 |
| MultiQueueRelaxed | SplitProducerConsumer | 2 | 1 | 3 | 30,430,625 | 10,143,266 |
| MultiQueueRelaxed | SplitProducerConsumer | 2 | 2 | 3 | 36,909,647 | 12,302,994 |
| MultiQueueRelaxed | SplitProducerConsumer | 2 | 4 | 3 | 45,766,269 | 15,255,076 |
| MultiQueueRelaxed | SplitProducerConsumer | 2 | 8 | 3 | 54,351,054 | 18,116,728 |
| MultiQueueRelaxed | NarrowKeyRange | 1 | 1 | 3 | 50,899,066 | 16,965,897 |
| MultiQueueRelaxed | NarrowKeyRange | 1 | 2 | 3 | 50,121,104 | 16,706,724 |
| MultiQueueRelaxed | NarrowKeyRange | 1 | 4 | 3 | 50,465,586 | 16,821,458 |
| MultiQueueRelaxed | NarrowKeyRange | 1 | 8 | 3 | 51,104,094 | 17,034,077 |
| MultiQueueRelaxed | NarrowKeyRange | 2 | 1 | 3 | 36,128,698 | 12,042,573 |
| MultiQueueRelaxed | NarrowKeyRange | 2 | 2 | 3 | 45,781,632 | 15,260,074 |
| MultiQueueRelaxed | NarrowKeyRange | 2 | 4 | 3 | 56,287,364 | 18,761,819 |
| MultiQueueRelaxed | NarrowKeyRange | 2 | 8 | 3 | 67,793,386 | 22,597,383 |
| MultiQueueRelaxed | Drain | 1 | 1 | 3 | 1,000,000 | 8,878,337 |
| MultiQueueRelaxed | Drain | 1 | 2 | 3 | 1,000,000 | 10,655,290 |
| MultiQueueRelaxed | Drain | 1 | 4 | 3 | 1,000,000 | 11,843,640 |
| MultiQueueRelaxed | Drain | 1 | 8 | 3 | 1,000,000 | 12,512,481 |
| MultiQueueRelaxed | Drain | 2 | 1 | 3 | 1,000,000 | 7,940,062 |
| MultiQueueRelaxed | Drain | 2 | 2 | 3 | 1,000,000 | 9,848,501 |
| MultiQueueRelaxed | Drain | 2 | 4 | 3 | 1,000,000 | 13,594,733 |
| MultiQueueRelaxed | Drain | 2 | 8 | 3 | 1,000,000 | 17,407,743 |
