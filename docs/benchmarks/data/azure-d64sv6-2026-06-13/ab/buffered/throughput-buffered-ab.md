# Bifrost CPQ Buffered-vs-Unbuffered Throughput A/B

```text
Bifrost CPQ throughput sweep — generated 2026-06-15 21:48:21Z
ProcessorCount: 64
OS: Ubuntu 24.04.4 LTS
CPU arch: X64
Runtime: .NET 10.0.9
GC mode: Workstation, Concurrent: Interactive
```

| Target | Workload | Threads | Buffer C | Stickiness | Window (s) | Total Ops | Ops/sec |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| MultiQueueRelaxed | UniformMixed5050 | 1 | 0 | 1 | 3 | 37,403,188 | 12,467,038 |
| MultiQueueRelaxed | UniformMixed5050 | 1 | 16 | 1 | 3 | 44,550,450 | 14,849,674 |
| MultiQueueRelaxed | UniformMixed5050 | 2 | 0 | 1 | 3 | 31,450,948 | 10,483,298 |
| MultiQueueRelaxed | UniformMixed5050 | 2 | 16 | 1 | 3 | 33,785,978 | 11,261,385 |
| MultiQueueRelaxed | UniformMixed5050 | 4 | 0 | 1 | 3 | 50,676,412 | 16,891,446 |
| MultiQueueRelaxed | UniformMixed5050 | 4 | 16 | 1 | 3 | 54,865,718 | 18,287,995 |
| MultiQueueRelaxed | UniformMixed5050 | 8 | 0 | 1 | 3 | 86,123,344 | 28,705,813 |
| MultiQueueRelaxed | UniformMixed5050 | 8 | 16 | 1 | 3 | 94,080,036 | 31,359,014 |
| MultiQueueRelaxed | UniformMixed5050 | 16 | 0 | 1 | 3 | 155,533,984 | 51,842,843 |
| MultiQueueRelaxed | UniformMixed5050 | 16 | 16 | 1 | 3 | 159,088,098 | 53,027,922 |
| MultiQueueRelaxed | UniformMixed5050 | 32 | 0 | 1 | 3 | 250,753,914 | 83,582,281 |
| MultiQueueRelaxed | UniformMixed5050 | 32 | 16 | 1 | 3 | 247,195,686 | 82,396,216 |
| MultiQueueRelaxed | UniformMixed5050 | 64 | 0 | 1 | 3 | 343,437,588 | 114,440,591 |
| MultiQueueRelaxed | UniformMixed5050 | 64 | 16 | 1 | 3 | 331,003,864 | 110,328,325 |
| MultiQueueRelaxed | UniformMixed5050 | 128 | 0 | 1 | 3 | 311,154,298 | 103,618,066 |
| MultiQueueRelaxed | UniformMixed5050 | 128 | 16 | 1 | 3 | 302,073,866 | 100,604,826 |
| MultiQueueRelaxed | SplitProducerConsumer | 1 | 0 | 1 | 3 | 34,236,445 | 11,411,932 |
| MultiQueueRelaxed | SplitProducerConsumer | 1 | 16 | 1 | 3 | 41,627,012 | 13,875,438 |
| MultiQueueRelaxed | SplitProducerConsumer | 2 | 0 | 1 | 3 | 30,509,489 | 10,169,606 |
| MultiQueueRelaxed | SplitProducerConsumer | 2 | 16 | 1 | 3 | 33,877,219 | 11,292,159 |
| MultiQueueRelaxed | SplitProducerConsumer | 4 | 0 | 1 | 3 | 50,224,505 | 16,741,130 |
| MultiQueueRelaxed | SplitProducerConsumer | 4 | 16 | 1 | 3 | 44,954,651 | 14,984,502 |
| MultiQueueRelaxed | SplitProducerConsumer | 8 | 0 | 1 | 3 | 86,542,528 | 28,846,700 |
| MultiQueueRelaxed | SplitProducerConsumer | 8 | 16 | 1 | 3 | 83,059,549 | 27,685,904 |
| MultiQueueRelaxed | SplitProducerConsumer | 16 | 0 | 1 | 3 | 147,729,873 | 49,242,168 |
| MultiQueueRelaxed | SplitProducerConsumer | 16 | 16 | 1 | 3 | 155,252,414 | 51,749,335 |
| MultiQueueRelaxed | SplitProducerConsumer | 32 | 0 | 1 | 3 | 250,646,259 | 83,545,670 |
| MultiQueueRelaxed | SplitProducerConsumer | 32 | 16 | 1 | 3 | 246,285,580 | 82,092,503 |
| MultiQueueRelaxed | SplitProducerConsumer | 64 | 0 | 1 | 3 | 340,045,547 | 113,322,988 |
| MultiQueueRelaxed | SplitProducerConsumer | 64 | 16 | 1 | 3 | 330,743,059 | 110,234,083 |
| MultiQueueRelaxed | SplitProducerConsumer | 128 | 0 | 1 | 3 | 291,000,048 | 96,853,444 |
| MultiQueueRelaxed | SplitProducerConsumer | 128 | 16 | 1 | 3 | 308,223,849 | 102,732,321 |
| MultiQueueRelaxed | NarrowKeyRange | 1 | 0 | 1 | 3 | 44,983,608 | 14,994,208 |
| MultiQueueRelaxed | NarrowKeyRange | 1 | 16 | 1 | 3 | 55,542,324 | 18,513,615 |
| MultiQueueRelaxed | NarrowKeyRange | 2 | 0 | 1 | 3 | 35,674,438 | 11,891,175 |
| MultiQueueRelaxed | NarrowKeyRange | 2 | 16 | 1 | 3 | 36,288,680 | 12,096,023 |
| MultiQueueRelaxed | NarrowKeyRange | 4 | 0 | 1 | 3 | 56,053,490 | 18,684,106 |
| MultiQueueRelaxed | NarrowKeyRange | 4 | 16 | 1 | 3 | 57,096,956 | 19,032,024 |
| MultiQueueRelaxed | NarrowKeyRange | 8 | 0 | 1 | 3 | 96,801,984 | 32,266,399 |
| MultiQueueRelaxed | NarrowKeyRange | 8 | 16 | 1 | 3 | 97,256,900 | 32,418,164 |
| MultiQueueRelaxed | NarrowKeyRange | 16 | 0 | 1 | 3 | 162,084,446 | 54,027,072 |
| MultiQueueRelaxed | NarrowKeyRange | 16 | 16 | 1 | 3 | 163,342,148 | 54,446,531 |
| MultiQueueRelaxed | NarrowKeyRange | 32 | 0 | 1 | 3 | 256,551,730 | 85,515,028 |
| MultiQueueRelaxed | NarrowKeyRange | 32 | 16 | 1 | 3 | 254,617,248 | 84,870,178 |
| MultiQueueRelaxed | NarrowKeyRange | 64 | 0 | 1 | 3 | 350,894,362 | 116,937,003 |
| MultiQueueRelaxed | NarrowKeyRange | 64 | 16 | 1 | 3 | 334,103,730 | 111,364,677 |
| MultiQueueRelaxed | NarrowKeyRange | 128 | 0 | 1 | 3 | 312,247,866 | 103,907,119 |
| MultiQueueRelaxed | NarrowKeyRange | 128 | 16 | 1 | 3 | 315,077,116 | 104,906,434 |
| MultiQueueRelaxed | Drain | 1 | 0 | 1 | 3 | 1,000,000 | 8,313,347 |
| MultiQueueRelaxed | Drain | 1 | 16 | 1 | 3 | 1,000,000 | 8,636,727 |
| MultiQueueRelaxed | Drain | 2 | 0 | 1 | 3 | 1,000,000 | 7,398,912 |
| MultiQueueRelaxed | Drain | 2 | 16 | 1 | 3 | 1,000,000 | 8,347,350 |
| MultiQueueRelaxed | Drain | 4 | 0 | 1 | 3 | 1,000,000 | 12,333,513 |
| MultiQueueRelaxed | Drain | 4 | 16 | 1 | 3 | 1,000,000 | 13,692,215 |
| MultiQueueRelaxed | Drain | 8 | 0 | 1 | 3 | 1,000,000 | 22,470,648 |
| MultiQueueRelaxed | Drain | 8 | 16 | 1 | 3 | 1,000,000 | 24,968,166 |
| MultiQueueRelaxed | Drain | 16 | 0 | 1 | 3 | 1,000,000 | 35,105,950 |
| MultiQueueRelaxed | Drain | 16 | 16 | 1 | 3 | 1,000,000 | 35,310,360 |
| MultiQueueRelaxed | Drain | 32 | 0 | 1 | 3 | 1,000,000 | 49,967,022 |
| MultiQueueRelaxed | Drain | 32 | 16 | 1 | 3 | 1,000,000 | 53,342,437 |
| MultiQueueRelaxed | Drain | 64 | 0 | 1 | 3 | 1,000,000 | 66,748,099 |
| MultiQueueRelaxed | Drain | 64 | 16 | 1 | 3 | 1,000,000 | 73,096,744 |
| MultiQueueRelaxed | Drain | 128 | 0 | 1 | 3 | 999,936 | 54,662,221 |
| MultiQueueRelaxed | Drain | 128 | 16 | 1 | 3 | 999,936 | 56,185,334 |
