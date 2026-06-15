# Bifrost CPQ Throughput Sweep

```text
Bifrost CPQ throughput sweep — generated 2026-06-15 21:44:42Z
ProcessorCount: 64
OS: Ubuntu 24.04.4 LTS
CPU arch: X64
Runtime: .NET 10.0.9
GC mode: Workstation, Concurrent: Interactive
```

| Target | Workload | Threads | Stickiness | Window (s) | Total Ops | Ops/sec |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| MultiQueueRelaxed | UniformMixed5050 | 1 | 1 | 3 | 37,266,198 | 12,421,536 |
| MultiQueueRelaxed | UniformMixed5050 | 2 | 1 | 3 | 32,263,822 | 10,754,361 |
| MultiQueueRelaxed | UniformMixed5050 | 4 | 1 | 3 | 51,785,104 | 17,261,272 |
| MultiQueueRelaxed | UniformMixed5050 | 8 | 1 | 3 | 90,058,500 | 30,018,474 |
| MultiQueueRelaxed | UniformMixed5050 | 16 | 1 | 3 | 150,869,376 | 50,288,552 |
| MultiQueueRelaxed | UniformMixed5050 | 32 | 1 | 3 | 250,984,676 | 83,659,314 |
| MultiQueueRelaxed | UniformMixed5050 | 64 | 1 | 3 | 354,973,454 | 118,320,237 |
| MultiQueueRelaxed | UniformMixed5050 | 128 | 1 | 3 | 313,014,542 | 104,200,237 |
| MultiQueueRelaxed | SplitProducerConsumer | 1 | 1 | 3 | 35,072,434 | 11,690,420 |
| MultiQueueRelaxed | SplitProducerConsumer | 2 | 1 | 3 | 31,915,163 | 10,638,016 |
| MultiQueueRelaxed | SplitProducerConsumer | 4 | 1 | 3 | 49,334,892 | 16,444,589 |
| MultiQueueRelaxed | SplitProducerConsumer | 8 | 1 | 3 | 84,975,333 | 28,324,426 |
| MultiQueueRelaxed | SplitProducerConsumer | 16 | 1 | 3 | 146,791,370 | 48,926,903 |
| MultiQueueRelaxed | SplitProducerConsumer | 32 | 1 | 3 | 248,464,258 | 82,818,744 |
| MultiQueueRelaxed | SplitProducerConsumer | 64 | 1 | 3 | 342,610,632 | 114,187,470 |
| MultiQueueRelaxed | SplitProducerConsumer | 128 | 1 | 3 | 279,637,077 | 93,138,308 |
| MultiQueueRelaxed | NarrowKeyRange | 1 | 1 | 3 | 45,415,454 | 15,138,256 |
| MultiQueueRelaxed | NarrowKeyRange | 2 | 1 | 3 | 35,291,390 | 11,763,593 |
| MultiQueueRelaxed | NarrowKeyRange | 4 | 1 | 3 | 58,259,642 | 19,419,468 |
| MultiQueueRelaxed | NarrowKeyRange | 8 | 1 | 3 | 100,020,796 | 33,339,664 |
| MultiQueueRelaxed | NarrowKeyRange | 16 | 1 | 3 | 161,517,658 | 53,837,771 |
| MultiQueueRelaxed | NarrowKeyRange | 32 | 1 | 3 | 266,598,506 | 88,863,358 |
| MultiQueueRelaxed | NarrowKeyRange | 64 | 1 | 3 | 349,656,296 | 116,546,788 |
| MultiQueueRelaxed | NarrowKeyRange | 128 | 1 | 3 | 318,439,172 | 106,027,025 |
| MultiQueueRelaxed | Drain | 1 | 1 | 3 | 1,000,000 | 7,999,533 |
| MultiQueueRelaxed | Drain | 2 | 1 | 3 | 1,000,000 | 7,485,047 |
| MultiQueueRelaxed | Drain | 4 | 1 | 3 | 1,000,000 | 12,310,889 |
| MultiQueueRelaxed | Drain | 8 | 1 | 3 | 1,000,000 | 21,634,953 |
| MultiQueueRelaxed | Drain | 16 | 1 | 3 | 1,000,000 | 36,179,581 |
| MultiQueueRelaxed | Drain | 32 | 1 | 3 | 1,000,000 | 46,393,379 |
| MultiQueueRelaxed | Drain | 64 | 1 | 3 | 1,000,000 | 62,563,737 |
| MultiQueueRelaxed | Drain | 128 | 1 | 3 | 999,936 | 54,667,600 |
| MultiQueueDequeueMin | UniformMixed5050 | 1 | 1 | 3 | 3,584,246 | 1,194,731 |
| MultiQueueDequeueMin | UniformMixed5050 | 2 | 1 | 3 | 3,168,464 | 1,056,141 |
| MultiQueueDequeueMin | UniformMixed5050 | 4 | 1 | 3 | 4,659,400 | 1,553,086 |
| MultiQueueDequeueMin | UniformMixed5050 | 8 | 1 | 3 | 7,472,182 | 2,490,547 |
| MultiQueueDequeueMin | UniformMixed5050 | 16 | 1 | 3 | 12,199,788 | 4,066,478 |
| MultiQueueDequeueMin | UniformMixed5050 | 32 | 1 | 3 | 18,801,060 | 6,266,760 |
| MultiQueueDequeueMin | UniformMixed5050 | 64 | 1 | 3 | 24,907,572 | 8,300,069 |
| MultiQueueDequeueMin | UniformMixed5050 | 128 | 1 | 3 | 26,215,468 | 8,733,497 |
| MultiQueueDequeueMin | SplitProducerConsumer | 1 | 1 | 3 | 33,546,234 | 11,181,695 |
| MultiQueueDequeueMin | SplitProducerConsumer | 2 | 1 | 3 | 20,871,660 | 6,957,032 |
| MultiQueueDequeueMin | SplitProducerConsumer | 4 | 1 | 3 | 37,458,803 | 12,485,850 |
| MultiQueueDequeueMin | SplitProducerConsumer | 8 | 1 | 3 | 69,311,607 | 23,103,190 |
| MultiQueueDequeueMin | SplitProducerConsumer | 16 | 1 | 3 | 126,011,902 | 42,003,241 |
| MultiQueueDequeueMin | SplitProducerConsumer | 32 | 1 | 3 | 184,046,617 | 61,346,832 |
| MultiQueueDequeueMin | SplitProducerConsumer | 64 | 1 | 3 | 231,209,167 | 77,063,151 |
| MultiQueueDequeueMin | SplitProducerConsumer | 128 | 1 | 3 | 190,672,304 | 63,501,484 |
| MultiQueueDequeueMin | NarrowKeyRange | 1 | 1 | 3 | 3,843,936 | 1,281,276 |
| MultiQueueDequeueMin | NarrowKeyRange | 2 | 1 | 3 | 3,195,664 | 1,065,193 |
| MultiQueueDequeueMin | NarrowKeyRange | 4 | 1 | 3 | 4,627,580 | 1,542,511 |
| MultiQueueDequeueMin | NarrowKeyRange | 8 | 1 | 3 | 7,524,776 | 2,508,211 |
| MultiQueueDequeueMin | NarrowKeyRange | 16 | 1 | 3 | 12,333,666 | 4,110,920 |
| MultiQueueDequeueMin | NarrowKeyRange | 32 | 1 | 3 | 18,373,692 | 6,124,365 |
| MultiQueueDequeueMin | NarrowKeyRange | 64 | 1 | 3 | 24,960,780 | 8,317,865 |
| MultiQueueDequeueMin | NarrowKeyRange | 128 | 1 | 3 | 23,355,420 | 7,773,805 |
| MultiQueueDequeueMin | Drain | 1 | 1 | 3 | 1,000,000 | 573,878 |
| MultiQueueDequeueMin | Drain | 2 | 1 | 3 | 1,000,000 | 634,137 |
| MultiQueueDequeueMin | Drain | 4 | 1 | 3 | 1,000,000 | 637,348 |
| MultiQueueDequeueMin | Drain | 8 | 1 | 3 | 1,000,000 | 638,546 |
| MultiQueueDequeueMin | Drain | 16 | 1 | 3 | 1,000,000 | 638,747 |
| MultiQueueDequeueMin | Drain | 32 | 1 | 3 | 1,000,000 | 638,071 |
| MultiQueueDequeueMin | Drain | 64 | 1 | 3 | 1,000,000 | 636,877 |
| MultiQueueDequeueMin | Drain | 128 | 1 | 3 | 999,936 | 630,075 |
| LockingBaseline | UniformMixed5050 | 1 | 1 | 3 | 65,614,878 | 21,871,151 |
| LockingBaseline | UniformMixed5050 | 2 | 1 | 3 | 31,014,752 | 10,337,977 |
| LockingBaseline | UniformMixed5050 | 4 | 1 | 3 | 18,190,454 | 6,063,051 |
| LockingBaseline | UniformMixed5050 | 8 | 1 | 3 | 23,939,912 | 7,979,787 |
| LockingBaseline | UniformMixed5050 | 16 | 1 | 3 | 22,913,532 | 7,637,590 |
| LockingBaseline | UniformMixed5050 | 32 | 1 | 3 | 15,222,602 | 5,074,052 |
| LockingBaseline | UniformMixed5050 | 64 | 1 | 3 | 21,475,084 | 7,158,173 |
| LockingBaseline | UniformMixed5050 | 128 | 1 | 3 | 25,559,556 | 8,519,647 |
| LockingBaseline | SplitProducerConsumer | 1 | 1 | 3 | 67,008,865 | 22,335,771 |
| LockingBaseline | SplitProducerConsumer | 2 | 1 | 3 | 32,532,543 | 10,843,856 |
| LockingBaseline | SplitProducerConsumer | 4 | 1 | 3 | 16,632,808 | 5,544,110 |
| LockingBaseline | SplitProducerConsumer | 8 | 1 | 3 | 15,685,668 | 5,228,421 |
| LockingBaseline | SplitProducerConsumer | 16 | 1 | 3 | 15,437,537 | 5,145,683 |
| LockingBaseline | SplitProducerConsumer | 32 | 1 | 3 | 13,906,336 | 4,635,345 |
| LockingBaseline | SplitProducerConsumer | 64 | 1 | 3 | 13,776,694 | 4,592,130 |
| LockingBaseline | SplitProducerConsumer | 128 | 1 | 3 | 14,037,954 | 4,679,196 |
| LockingBaseline | NarrowKeyRange | 1 | 1 | 3 | 130,568,790 | 43,522,099 |
| LockingBaseline | NarrowKeyRange | 2 | 1 | 3 | 37,642,192 | 12,546,546 |
| LockingBaseline | NarrowKeyRange | 4 | 1 | 3 | 21,454,090 | 7,151,106 |
| LockingBaseline | NarrowKeyRange | 8 | 1 | 3 | 17,978,080 | 5,992,564 |
| LockingBaseline | NarrowKeyRange | 16 | 1 | 3 | 21,338,158 | 7,112,527 |
| LockingBaseline | NarrowKeyRange | 32 | 1 | 3 | 22,684,350 | 7,561,277 |
| LockingBaseline | NarrowKeyRange | 64 | 1 | 3 | 23,493,290 | 7,830,892 |
| LockingBaseline | NarrowKeyRange | 128 | 1 | 3 | 19,586,100 | 6,528,106 |
| LockingBaseline | Drain | 1 | 1 | 3 | 1,000,000 | 13,187,619 |
| LockingBaseline | Drain | 2 | 1 | 3 | 1,000,000 | 5,996,254 |
| LockingBaseline | Drain | 4 | 1 | 3 | 1,000,000 | 5,990,876 |
| LockingBaseline | Drain | 8 | 1 | 3 | 1,000,000 | 5,067,617 |
| LockingBaseline | Drain | 16 | 1 | 3 | 1,000,000 | 5,573,971 |
| LockingBaseline | Drain | 32 | 1 | 3 | 1,000,000 | 5,239,451 |
| LockingBaseline | Drain | 64 | 1 | 3 | 1,000,000 | 5,390,307 |
| LockingBaseline | Drain | 128 | 1 | 3 | 999,936 | 5,265,462 |
