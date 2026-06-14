# Bifrost CPQ Throughput Sweep

```text
Bifrost CPQ throughput sweep — generated 2026-06-13 22:55:26Z
ProcessorCount: 64
OS: Ubuntu 24.04.4 LTS
CPU arch: X64
Runtime: .NET 10.0.9
GC mode: Workstation, Concurrent: Interactive
```

| Target | Workload | Threads | Stickiness | Window (s) | Total Ops | Ops/sec |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| MultiQueueRelaxed | UniformMixed5050 | 1 | 1 | 3 | 39,340,420 | 13,112,946 |
| MultiQueueRelaxed | UniformMixed5050 | 2 | 1 | 3 | 32,155,860 | 10,718,317 |
| MultiQueueRelaxed | UniformMixed5050 | 4 | 1 | 3 | 51,035,508 | 17,011,350 |
| MultiQueueRelaxed | UniformMixed5050 | 8 | 1 | 3 | 91,084,286 | 30,359,269 |
| MultiQueueRelaxed | UniformMixed5050 | 16 | 1 | 3 | 153,395,338 | 51,130,324 |
| MultiQueueRelaxed | UniformMixed5050 | 32 | 1 | 3 | 236,800,488 | 78,932,146 |
| MultiQueueRelaxed | UniformMixed5050 | 64 | 1 | 3 | 340,746,780 | 113,575,926 |
| MultiQueueRelaxed | UniformMixed5050 | 128 | 1 | 3 | 319,032,082 | 106,170,057 |
| MultiQueueRelaxed | SplitProducerConsumer | 1 | 1 | 3 | 36,173,879 | 12,057,621 |
| MultiQueueRelaxed | SplitProducerConsumer | 2 | 1 | 3 | 30,460,838 | 10,153,247 |
| MultiQueueRelaxed | SplitProducerConsumer | 4 | 1 | 3 | 48,834,465 | 16,277,943 |
| MultiQueueRelaxed | SplitProducerConsumer | 8 | 1 | 3 | 86,757,164 | 28,918,215 |
| MultiQueueRelaxed | SplitProducerConsumer | 16 | 1 | 3 | 145,041,283 | 48,345,478 |
| MultiQueueRelaxed | SplitProducerConsumer | 32 | 1 | 3 | 240,186,883 | 80,059,636 |
| MultiQueueRelaxed | SplitProducerConsumer | 64 | 1 | 3 | 330,017,900 | 109,966,742 |
| MultiQueueRelaxed | SplitProducerConsumer | 128 | 1 | 3 | 333,233,534 | 110,922,535 |
| MultiQueueRelaxed | NarrowKeyRange | 1 | 1 | 3 | 43,466,418 | 14,487,893 |
| MultiQueueRelaxed | NarrowKeyRange | 2 | 1 | 3 | 35,139,610 | 11,712,817 |
| MultiQueueRelaxed | NarrowKeyRange | 4 | 1 | 3 | 54,411,694 | 18,136,740 |
| MultiQueueRelaxed | NarrowKeyRange | 8 | 1 | 3 | 93,870,586 | 31,289,383 |
| MultiQueueRelaxed | NarrowKeyRange | 16 | 1 | 3 | 155,955,366 | 51,983,647 |
| MultiQueueRelaxed | NarrowKeyRange | 32 | 1 | 3 | 245,218,496 | 81,737,763 |
| MultiQueueRelaxed | NarrowKeyRange | 64 | 1 | 3 | 334,686,040 | 111,532,821 |
| MultiQueueRelaxed | NarrowKeyRange | 128 | 1 | 3 | 317,246,898 | 105,648,484 |
| MultiQueueRelaxed | Drain | 1 | 1 | 3 | 1,000,000 | 8,043,713 |
| MultiQueueRelaxed | Drain | 2 | 1 | 3 | 1,000,000 | 7,206,568 |
| MultiQueueRelaxed | Drain | 4 | 1 | 3 | 1,000,000 | 12,017,353 |
| MultiQueueRelaxed | Drain | 8 | 1 | 3 | 1,000,000 | 20,701,876 |
| MultiQueueRelaxed | Drain | 16 | 1 | 3 | 1,000,000 | 34,041,510 |
| MultiQueueRelaxed | Drain | 32 | 1 | 3 | 1,000,000 | 45,903,355 |
| MultiQueueRelaxed | Drain | 64 | 1 | 3 | 1,000,000 | 65,773,463 |
| MultiQueueRelaxed | Drain | 128 | 1 | 3 | 999,936 | 53,643,484 |
| MultiQueueDequeueMin | UniformMixed5050 | 1 | 1 | 3 | 3,258,504 | 1,086,136 |
| MultiQueueDequeueMin | UniformMixed5050 | 2 | 1 | 3 | 3,028,402 | 1,009,433 |
| MultiQueueDequeueMin | UniformMixed5050 | 4 | 1 | 3 | 4,482,652 | 1,494,172 |
| MultiQueueDequeueMin | UniformMixed5050 | 8 | 1 | 3 | 7,597,154 | 2,532,317 |
| MultiQueueDequeueMin | UniformMixed5050 | 16 | 1 | 3 | 12,046,666 | 4,015,438 |
| MultiQueueDequeueMin | UniformMixed5050 | 32 | 1 | 3 | 19,384,826 | 6,461,415 |
| MultiQueueDequeueMin | UniformMixed5050 | 64 | 1 | 3 | 26,438,628 | 8,811,513 |
| MultiQueueDequeueMin | UniformMixed5050 | 128 | 1 | 3 | 25,024,206 | 8,327,111 |
| MultiQueueDequeueMin | SplitProducerConsumer | 1 | 1 | 3 | 33,546,339 | 11,181,573 |
| MultiQueueDequeueMin | SplitProducerConsumer | 2 | 1 | 3 | 17,774,782 | 5,924,729 |
| MultiQueueDequeueMin | SplitProducerConsumer | 4 | 1 | 3 | 34,923,290 | 11,640,684 |
| MultiQueueDequeueMin | SplitProducerConsumer | 8 | 1 | 3 | 67,279,786 | 22,425,861 |
| MultiQueueDequeueMin | SplitProducerConsumer | 16 | 1 | 3 | 118,724,934 | 39,574,303 |
| MultiQueueDequeueMin | SplitProducerConsumer | 32 | 1 | 3 | 177,928,010 | 59,307,342 |
| MultiQueueDequeueMin | SplitProducerConsumer | 64 | 1 | 3 | 227,373,754 | 75,771,846 |
| MultiQueueDequeueMin | SplitProducerConsumer | 128 | 1 | 3 | 169,547,592 | 56,433,839 |
| MultiQueueDequeueMin | NarrowKeyRange | 1 | 1 | 3 | 3,501,386 | 1,167,104 |
| MultiQueueDequeueMin | NarrowKeyRange | 2 | 1 | 3 | 3,148,802 | 1,049,565 |
| MultiQueueDequeueMin | NarrowKeyRange | 4 | 1 | 3 | 4,571,790 | 1,523,885 |
| MultiQueueDequeueMin | NarrowKeyRange | 8 | 1 | 3 | 7,619,058 | 2,539,614 |
| MultiQueueDequeueMin | NarrowKeyRange | 16 | 1 | 3 | 12,483,780 | 4,160,942 |
| MultiQueueDequeueMin | NarrowKeyRange | 32 | 1 | 3 | 19,119,290 | 6,372,937 |
| MultiQueueDequeueMin | NarrowKeyRange | 64 | 1 | 3 | 26,315,736 | 8,770,422 |
| MultiQueueDequeueMin | NarrowKeyRange | 128 | 1 | 3 | 25,955,578 | 8,642,663 |
| MultiQueueDequeueMin | Drain | 1 | 1 | 3 | 1,000,000 | 542,725 |
| MultiQueueDequeueMin | Drain | 2 | 1 | 3 | 1,000,000 | 583,994 |
| MultiQueueDequeueMin | Drain | 4 | 1 | 3 | 1,000,000 | 585,063 |
| MultiQueueDequeueMin | Drain | 8 | 1 | 3 | 1,000,000 | 588,308 |
| MultiQueueDequeueMin | Drain | 16 | 1 | 3 | 1,000,000 | 585,507 |
| MultiQueueDequeueMin | Drain | 32 | 1 | 3 | 1,000,000 | 580,704 |
| MultiQueueDequeueMin | Drain | 64 | 1 | 3 | 1,000,000 | 584,839 |
| MultiQueueDequeueMin | Drain | 128 | 1 | 3 | 999,936 | 585,439 |
| LockingBaseline | UniformMixed5050 | 1 | 1 | 3 | 66,785,394 | 22,261,189 |
| LockingBaseline | UniformMixed5050 | 2 | 1 | 3 | 28,009,350 | 9,336,193 |
| LockingBaseline | UniformMixed5050 | 4 | 1 | 3 | 26,637,870 | 8,879,013 |
| LockingBaseline | UniformMixed5050 | 8 | 1 | 3 | 28,278,256 | 9,425,834 |
| LockingBaseline | UniformMixed5050 | 16 | 1 | 3 | 21,433,796 | 7,144,438 |
| LockingBaseline | UniformMixed5050 | 32 | 1 | 3 | 26,593,538 | 8,864,251 |
| LockingBaseline | UniformMixed5050 | 64 | 1 | 3 | 24,113,342 | 8,037,568 |
| LockingBaseline | UniformMixed5050 | 128 | 1 | 3 | 16,335,516 | 5,445,053 |
| LockingBaseline | SplitProducerConsumer | 1 | 1 | 3 | 67,008,865 | 22,335,690 |
| LockingBaseline | SplitProducerConsumer | 2 | 1 | 3 | 36,007,263 | 12,002,087 |
| LockingBaseline | SplitProducerConsumer | 4 | 1 | 3 | 17,907,524 | 5,969,053 |
| LockingBaseline | SplitProducerConsumer | 8 | 1 | 3 | 17,280,019 | 5,759,882 |
| LockingBaseline | SplitProducerConsumer | 16 | 1 | 3 | 17,871,120 | 5,956,926 |
| LockingBaseline | SplitProducerConsumer | 32 | 1 | 3 | 13,523,984 | 4,507,896 |
| LockingBaseline | SplitProducerConsumer | 64 | 1 | 3 | 15,684,388 | 5,228,013 |
| LockingBaseline | SplitProducerConsumer | 128 | 1 | 3 | 15,823,681 | 5,274,439 |
| LockingBaseline | NarrowKeyRange | 1 | 1 | 3 | 130,745,016 | 43,580,359 |
| LockingBaseline | NarrowKeyRange | 2 | 1 | 3 | 45,102,826 | 15,033,995 |
| LockingBaseline | NarrowKeyRange | 4 | 1 | 3 | 24,851,266 | 8,283,576 |
| LockingBaseline | NarrowKeyRange | 8 | 1 | 3 | 18,594,998 | 6,198,183 |
| LockingBaseline | NarrowKeyRange | 16 | 1 | 3 | 19,004,944 | 6,334,915 |
| LockingBaseline | NarrowKeyRange | 32 | 1 | 3 | 21,792,268 | 7,264,012 |
| LockingBaseline | NarrowKeyRange | 64 | 1 | 3 | 22,534,642 | 7,511,339 |
| LockingBaseline | NarrowKeyRange | 128 | 1 | 3 | 15,122,766 | 5,040,782 |
| LockingBaseline | Drain | 1 | 1 | 3 | 1,000,000 | 12,688,230 |
| LockingBaseline | Drain | 2 | 1 | 3 | 1,000,000 | 6,293,848 |
| LockingBaseline | Drain | 4 | 1 | 3 | 1,000,000 | 6,358,738 |
| LockingBaseline | Drain | 8 | 1 | 3 | 1,000,000 | 5,826,097 |
| LockingBaseline | Drain | 16 | 1 | 3 | 1,000,000 | 5,506,171 |
| LockingBaseline | Drain | 32 | 1 | 3 | 1,000,000 | 4,760,853 |
| LockingBaseline | Drain | 64 | 1 | 3 | 1,000,000 | 5,072,445 |
| LockingBaseline | Drain | 128 | 1 | 3 | 999,936 | 5,283,387 |
