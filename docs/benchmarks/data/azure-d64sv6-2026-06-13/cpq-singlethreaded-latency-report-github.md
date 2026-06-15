```

BenchmarkDotNet v0.14.0, Ubuntu 24.04.4 LTS (Noble Numbat)
INTEL XEON PLATINUM 8573C, 1 CPU, 64 logical and 32 physical cores
.NET SDK 10.0.301
  [Host]   : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  ShortRun : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                               | Population | Mean         | Error         | StdDev     | Median       | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------------- |----------- |-------------:|--------------:|-----------:|-------------:|------:|--------:|----------:|------------:|
| **MultiQueue_EnqueueDequeue_Int**        | **10**         |   **145.624 ns** |     **8.4720 ns** |  **0.4644 ns** |   **145.400 ns** |  **3.36** |    **0.30** |         **-** |        **0.00** |
| MultiQueueSticky4_EnqueueDequeue_Int | 10         |   126.993 ns |     4.1223 ns |  0.2260 ns |   127.056 ns |  2.93 |    0.26 |         - |        0.00 |
| Locking_EnqueueDequeue_Int           | 10         |    38.196 ns |     1.1757 ns |  0.0644 ns |    38.204 ns |  0.88 |    0.08 |         - |        0.00 |
| RawLocked_EnqueueDequeue_Int         | 10         |    20.426 ns |     0.4453 ns |  0.0244 ns |    20.439 ns |  0.47 |    0.04 |         - |        0.00 |
| RawUnlocked_EnqueueDequeue_Int       | 10         |     8.963 ns |     1.6533 ns |  0.0906 ns |     9.004 ns |  0.21 |    0.02 |         - |        0.00 |
| MultiQueue_EnqueueDequeue_String     | 10         |   156.120 ns |     4.1614 ns |  0.2281 ns |   156.036 ns |  3.61 |    0.32 |         - |        0.00 |
| MultiQueue_Enqueue_Int               | 10         |    43.619 ns |    87.4799 ns |  4.7951 ns |    40.979 ns |  1.01 |    0.13 |      65 B |        1.00 |
| MultiQueueSticky4_Enqueue_Int        | 10         |    40.617 ns |    90.9878 ns |  4.9874 ns |    37.783 ns |  0.94 |    0.13 |      62 B |        0.95 |
| Locking_Enqueue_Int                  | 10         |    22.454 ns |   128.3999 ns |  7.0380 ns |    18.393 ns |  0.52 |    0.15 |         - |        0.00 |
| RawLocked_Enqueue_Int                | 10         |    22.711 ns |   133.2407 ns |  7.3034 ns |    18.541 ns |  0.52 |    0.15 |         - |        0.00 |
| RawUnlocked_Enqueue_Int              | 10         |     6.011 ns |   125.0529 ns |  6.8546 ns |     2.054 ns |  0.14 |    0.14 |         - |        0.00 |
| MultiQueue_Enqueue_String            | 10         |   160.699 ns |   241.2266 ns | 13.2224 ns |   153.486 ns |  3.71 |    0.43 |     127 B |        1.95 |
|                                      |            |              |               |            |              |       |         |           |             |
| **MultiQueue_EnqueueDequeue_Int**        | **1000**       |    **78.348 ns** |     **1.3412 ns** |  **0.0735 ns** |    **78.369 ns** |  **1.79** |    **0.15** |         **-** |        **0.00** |
| MultiQueueSticky4_EnqueueDequeue_Int | 1000       |    68.404 ns |     2.8451 ns |  0.1560 ns |    68.456 ns |  1.56 |    0.13 |         - |        0.00 |
| Locking_EnqueueDequeue_Int           | 1000       |    62.106 ns |     5.3673 ns |  0.2942 ns |    62.254 ns |  1.42 |    0.12 |         - |        0.00 |
| RawLocked_EnqueueDequeue_Int         | 1000       |    54.244 ns |     1.8299 ns |  0.1003 ns |    54.195 ns |  1.24 |    0.11 |         - |        0.00 |
| RawUnlocked_EnqueueDequeue_Int       | 1000       |    51.854 ns |     2.8699 ns |  0.1573 ns |    51.783 ns |  1.18 |    0.10 |         - |        0.00 |
| MultiQueue_EnqueueDequeue_String     | 1000       |   358.592 ns |     6.9350 ns |  0.3801 ns |   358.438 ns |  8.19 |    0.70 |         - |        0.00 |
| MultiQueue_Enqueue_Int               | 1000       |    44.074 ns |    84.6735 ns |  4.6412 ns |    41.409 ns |  1.01 |    0.13 |      68 B |        1.00 |
| MultiQueueSticky4_Enqueue_Int        | 1000       |    41.176 ns |    99.4573 ns |  5.4516 ns |    38.118 ns |  0.94 |    0.14 |      62 B |        0.91 |
| Locking_Enqueue_Int                  | 1000       |    22.824 ns |   132.3874 ns |  7.2566 ns |    18.770 ns |  0.52 |    0.15 |         - |        0.00 |
| RawLocked_Enqueue_Int                | 1000       |    22.709 ns |   133.4017 ns |  7.3122 ns |    18.526 ns |  0.52 |    0.15 |         - |        0.00 |
| RawUnlocked_Enqueue_Int              | 1000       |     6.014 ns |   125.2677 ns |  6.8663 ns |     2.051 ns |  0.14 |    0.14 |         - |        0.00 |
| MultiQueue_Enqueue_String            | 1000       |   169.629 ns |   235.5598 ns | 12.9118 ns |   162.447 ns |  3.88 |    0.42 |     132 B |        1.94 |
|                                      |            |              |               |            |              |       |         |           |             |
| **MultiQueue_EnqueueDequeue_Int**        | **100000**     |   **122.273 ns** |     **3.8171 ns** |  **0.2092 ns** |   **122.268 ns** |  **2.76** |    **0.33** |         **-** |        **0.00** |
| MultiQueueSticky4_EnqueueDequeue_Int | 100000     |   107.128 ns |     3.7074 ns |  0.2032 ns |   107.031 ns |  2.41 |    0.29 |         - |        0.00 |
| Locking_EnqueueDequeue_Int           | 100000     |    82.528 ns |     7.5753 ns |  0.4152 ns |    82.334 ns |  1.86 |    0.22 |         - |        0.00 |
| RawLocked_EnqueueDequeue_Int         | 100000     |    75.320 ns |     4.6706 ns |  0.2560 ns |    75.330 ns |  1.70 |    0.20 |         - |        0.00 |
| RawUnlocked_EnqueueDequeue_Int       | 100000     |    60.532 ns |     4.0785 ns |  0.2236 ns |    60.500 ns |  1.36 |    0.16 |         - |        0.00 |
| MultiQueue_EnqueueDequeue_String     | 100000     |   997.961 ns |    45.4855 ns |  2.4932 ns |   997.433 ns | 22.50 |    2.68 |         - |        0.00 |
| MultiQueue_Enqueue_Int               | 100000     |    44.977 ns |   122.6074 ns |  6.7205 ns |    41.103 ns |  1.01 |    0.18 |      36 B |        1.00 |
| MultiQueueSticky4_Enqueue_Int        | 100000     |    40.897 ns |   104.3504 ns |  5.7198 ns |    37.642 ns |  0.92 |    0.16 |      53 B |        1.47 |
| Locking_Enqueue_Int                  | 100000     |    22.743 ns |   128.3224 ns |  7.0338 ns |    18.799 ns |  0.51 |    0.15 |         - |        0.00 |
| RawLocked_Enqueue_Int                | 100000     |    22.954 ns |   133.0129 ns |  7.2909 ns |    18.767 ns |  0.52 |    0.16 |         - |        0.00 |
| RawUnlocked_Enqueue_Int              | 100000     |     6.042 ns |   125.0391 ns |  6.8538 ns |     2.090 ns |  0.14 |    0.14 |         - |        0.00 |
| MultiQueue_Enqueue_String            | 100000     |   168.514 ns |   393.0267 ns | 21.5431 ns |   157.145 ns |  3.80 |    0.62 |      44 B |        1.22 |
|                                      |            |              |               |            |              |       |         |           |             |
| **MultiQueue_EnqueueDequeue_Int**        | **1000000**    |   **170.411 ns** |    **27.5470 ns** |  **1.5099 ns** |   **170.654 ns** |  **3.59** |    **0.55** |         **-** |          **NA** |
| MultiQueueSticky4_EnqueueDequeue_Int | 1000000    |   158.276 ns |    10.2543 ns |  0.5621 ns |   158.457 ns |  3.34 |    0.51 |         - |          NA |
| Locking_EnqueueDequeue_Int           | 1000000    |    89.373 ns |     5.3385 ns |  0.2926 ns |    89.523 ns |  1.88 |    0.29 |         - |          NA |
| RawLocked_EnqueueDequeue_Int         | 1000000    |    80.183 ns |    10.9626 ns |  0.6009 ns |    80.203 ns |  1.69 |    0.26 |         - |          NA |
| RawUnlocked_EnqueueDequeue_Int       | 1000000    |    67.381 ns |     6.8562 ns |  0.3758 ns |    67.176 ns |  1.42 |    0.22 |         - |          NA |
| MultiQueue_EnqueueDequeue_String     | 1000000    | 1,281.597 ns | 1,553.6137 ns | 85.1588 ns | 1,319.525 ns | 27.01 |    4.43 |         - |          NA |
| MultiQueue_Enqueue_Int               | 1000000    |    48.578 ns |   174.1118 ns |  9.5437 ns |    43.884 ns |  1.02 |    0.24 |         - |          NA |
| MultiQueueSticky4_Enqueue_Int        | 1000000    |    43.692 ns |   173.6142 ns |  9.5164 ns |    38.223 ns |  0.92 |    0.23 |       1 B |          NA |
| Locking_Enqueue_Int                  | 1000000    |    23.569 ns |   133.4528 ns |  7.3150 ns |    19.370 ns |  0.50 |    0.15 |         - |          NA |
| RawLocked_Enqueue_Int                | 1000000    |    22.991 ns |   129.1480 ns |  7.0790 ns |    19.040 ns |  0.48 |    0.15 |         - |          NA |
| RawUnlocked_Enqueue_Int              | 1000000    |     6.236 ns |   130.4981 ns |  7.1530 ns |     2.111 ns |  0.13 |    0.13 |         - |          NA |
| MultiQueue_Enqueue_String            | 1000000    |   170.365 ns |   471.1866 ns | 25.8273 ns |   156.609 ns |  3.59 |    0.73 |         - |          NA |
