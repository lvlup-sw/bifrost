# Bifrost CPQ Consumer-Shaped Soak (DR-8)

```text
Bifrost CPQ consumer-shaped soak (DR-8) — generated 2026-06-16 02:07:12Z
ProcessorCount: 32
OS: Pop!_OS 24.04 LTS
CPU arch: X64
Runtime: .NET 10.0.6
GC mode: Workstation, Concurrent: Interactive
```

## Per-class queue-wait and fairness

| Binding | Workers | Window (s) | Class | Offered | Rejected | Dispatched | Offered % | Dispatch % | p50 (ms) | p95 (ms) | p99 (ms) | max (ms) |
| --- | ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| LockingPriority | 2 | 600 | Interactive | 111 | 0 | 107 | 6.9 | 9.7 | 16,002 | 33,375 | 40,677 | 40,791 |
| LockingPriority | 2 | 600 | Default | 111 | 0 | 102 | 6.9 | 9.2 | 52,014 | 66,575 | 73,167 | 74,922 |
| LockingPriority | 2 | 600 | Batch | 1,381 | 415 | 896 | 86.2 | 81.1 | 51,833 | 65,193 | 72,543 | 74,680 |
| LockingPriority | 8 | 600 | Interactive | 656 | 0 | 656 | 14.1 | 15.1 | 160 | 1,003 | 1,562 | 2,031 |
| LockingPriority | 8 | 600 | Default | 1,039 | 32 | 1,007 | 22.4 | 23.2 | 10,465 | 16,986 | 19,486 | 20,121 |
| LockingPriority | 8 | 600 | Batch | 2,942 | 266 | 2,671 | 63.4 | 61.6 | 9,601 | 16,199 | 18,264 | 19,870 |
| MultiQueuePriority | 2 | 600 | Interactive | 109 | 0 | 106 | 6.9 | 9.4 | 21,444 | 93,318 | 128,359 | 148,268 |
| MultiQueuePriority | 2 | 600 | Default | 109 | 0 | 103 | 6.9 | 9.1 | 36,069 | 133,023 | 153,758 | 165,821 |
| MultiQueuePriority | 2 | 600 | Batch | 1,373 | 391 | 922 | 86.3 | 81.5 | 38,310 | 120,090 | 160,215 | 225,457 |
| MultiQueuePriority | 8 | 600 | Interactive | 663 | 0 | 658 | 14.2 | 15.2 | 3,040 | 15,417 | 18,878 | 25,497 |
| MultiQueuePriority | 8 | 600 | Default | 1,035 | 17 | 990 | 22.2 | 22.9 | 5,484 | 21,078 | 24,364 | 47,448 |
| MultiQueuePriority | 8 | 600 | Batch | 2,966 | 263 | 2,672 | 63.6 | 61.9 | 6,287 | 20,490 | 25,834 | 44,779 |

## Run-level: occupancy, allocation stability, starvation probe

| Binding | Workers | Residual | Occ mean % | Occ max % | In-band % | Alloc MiB/min (mean) | Alloc MiB/min (min–max) | Gen0 | Gen1 | Gen2 | Max batch wait (s) | Boost window (s) | Depth-adj bound (s) | Bound held |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| LockingPriority | 2 | 83 | 72.7 | 90.6 | 12.7 | 0.05 | 0.00–0.88 | 0 | 0 | 0 | 74.7 | 30 | 98.8 | HELD |
| LockingPriority | 8 | 5 | 42.2 | 95.3 | 22.0 | 0.16 | 0.00–0.37 | 0 | 0 | 0 | 19.9 | 30 | 47.2 | HELD |
| MultiQueuePriority | 2 | 69 | 72.1 | 90.6 | 13.3 | 0.05 | 0.00–0.37 | 0 | 0 | 0 | 225.5 | 30 | 98.8 | EXCEEDED |
| MultiQueuePriority | 8 | 64 | 41.7 | 96.1 | 25.7 | 0.17 | 0.00–0.37 | 0 | 0 | 0 | 44.8 | 30 | 47.2 | HELD |
