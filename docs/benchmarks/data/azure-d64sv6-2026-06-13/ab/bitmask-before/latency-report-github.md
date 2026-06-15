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
| **MultiQueue_EnqueueDequeue_Int**        | **10**         |   **264.162 ns** |    **25.7411 ns** |  **1.4110 ns** |   **263.771 ns** |  **6.14** |    **0.56** |         **-** |        **0.00** |
| MultiQueueSticky4_EnqueueDequeue_Int | 10         |   249.020 ns |    27.7355 ns |  1.5203 ns |   248.485 ns |  5.78 |    0.53 |         - |        0.00 |
| Locking_EnqueueDequeue_Int           | 10         |    38.095 ns |     4.7436 ns |  0.2600 ns |    37.962 ns |  0.88 |    0.08 |         - |        0.00 |
| RawLocked_EnqueueDequeue_Int         | 10         |    20.344 ns |     0.6636 ns |  0.0364 ns |    20.348 ns |  0.47 |    0.04 |         - |        0.00 |
| RawUnlocked_EnqueueDequeue_Int       | 10         |     7.674 ns |     7.9136 ns |  0.4338 ns |     7.787 ns |  0.18 |    0.02 |         - |        0.00 |
| MultiQueue_EnqueueDequeue_String     | 10         |   366.142 ns |    13.8427 ns |  0.7588 ns |   365.734 ns |  8.50 |    0.78 |         - |        0.00 |
| MultiQueue_Enqueue_Int               | 10         |    43.398 ns |    89.1374 ns |  4.8859 ns |    40.607 ns |  1.01 |    0.14 |      65 B |        1.00 |
| MultiQueueSticky4_Enqueue_Int        | 10         |    40.312 ns |    90.3718 ns |  4.9536 ns |    37.454 ns |  0.94 |    0.13 |      66 B |        1.02 |
| Locking_Enqueue_Int                  | 10         |    22.409 ns |   127.3782 ns |  6.9820 ns |    18.386 ns |  0.52 |    0.15 |         - |        0.00 |
| RawLocked_Enqueue_Int                | 10         |    22.762 ns |   128.7243 ns |  7.0558 ns |    18.706 ns |  0.53 |    0.15 |         - |        0.00 |
| RawUnlocked_Enqueue_Int              | 10         |     5.976 ns |   124.2958 ns |  6.8131 ns |     2.043 ns |  0.14 |    0.14 |         - |        0.00 |
| MultiQueue_Enqueue_String            | 10         |   158.918 ns |   237.3184 ns | 13.0082 ns |   151.476 ns |  3.69 |    0.43 |     131 B |        2.02 |
|                                      |            |              |               |            |              |       |         |           |             |
| **MultiQueue_EnqueueDequeue_Int**        | **1000**       |    **77.335 ns** |     **3.1616 ns** |  **0.1733 ns** |    **77.303 ns** |  **1.84** |    **0.17** |         **-** |        **0.00** |
| MultiQueueSticky4_EnqueueDequeue_Int | 1000       |    65.471 ns |     4.0752 ns |  0.2234 ns |    65.464 ns |  1.56 |    0.14 |         - |        0.00 |
| Locking_EnqueueDequeue_Int           | 1000       |    62.951 ns |     2.1033 ns |  0.1153 ns |    62.968 ns |  1.50 |    0.14 |         - |        0.00 |
| RawLocked_EnqueueDequeue_Int         | 1000       |    57.928 ns |     0.9565 ns |  0.0524 ns |    57.916 ns |  1.38 |    0.13 |         - |        0.00 |
| RawUnlocked_EnqueueDequeue_Int       | 1000       |    53.501 ns |     2.0426 ns |  0.1120 ns |    53.542 ns |  1.28 |    0.12 |         - |        0.00 |
| MultiQueue_EnqueueDequeue_String     | 1000       |   351.813 ns |    17.5674 ns |  0.9629 ns |   352.048 ns |  8.38 |    0.77 |         - |        0.00 |
| MultiQueue_Enqueue_Int               | 1000       |    42.294 ns |    86.6751 ns |  4.7510 ns |    39.612 ns |  1.01 |    0.13 |      65 B |        1.00 |
| MultiQueueSticky4_Enqueue_Int        | 1000       |    39.759 ns |    87.7363 ns |  4.8091 ns |    36.995 ns |  0.95 |    0.13 |      65 B |        1.00 |
| Locking_Enqueue_Int                  | 1000       |    22.445 ns |   127.5410 ns |  6.9910 ns |    18.420 ns |  0.53 |    0.15 |         - |        0.00 |
| RawLocked_Enqueue_Int                | 1000       |    22.626 ns |   131.1330 ns |  7.1878 ns |    18.488 ns |  0.54 |    0.16 |         - |        0.00 |
| RawUnlocked_Enqueue_Int              | 1000       |     6.258 ns |   129.2673 ns |  7.0856 ns |     2.174 ns |  0.15 |    0.15 |         - |        0.00 |
| MultiQueue_Enqueue_String            | 1000       |   168.972 ns |   232.5781 ns | 12.7484 ns |   161.892 ns |  4.03 |    0.45 |     127 B |        1.95 |
|                                      |            |              |               |            |              |       |         |           |             |
| **MultiQueue_EnqueueDequeue_Int**        | **100000**     |   **118.886 ns** |     **2.4828 ns** |  **0.1361 ns** |   **118.960 ns** |  **2.76** |    **0.34** |         **-** |        **0.00** |
| MultiQueueSticky4_EnqueueDequeue_Int | 100000     |   104.004 ns |     4.2366 ns |  0.2322 ns |   104.132 ns |  2.42 |    0.30 |         - |        0.00 |
| Locking_EnqueueDequeue_Int           | 100000     |    80.332 ns |     2.1047 ns |  0.1154 ns |    80.314 ns |  1.87 |    0.23 |         - |        0.00 |
| RawLocked_EnqueueDequeue_Int         | 100000     |    71.776 ns |     4.4809 ns |  0.2456 ns |    71.751 ns |  1.67 |    0.21 |         - |        0.00 |
| RawUnlocked_EnqueueDequeue_Int       | 100000     |    64.875 ns |     8.5443 ns |  0.4683 ns |    64.651 ns |  1.51 |    0.19 |         - |        0.00 |
| MultiQueue_EnqueueDequeue_String     | 100000     |   982.106 ns |    76.9195 ns |  4.2162 ns |   980.436 ns | 22.82 |    2.85 |         - |        0.00 |
| MultiQueue_Enqueue_Int               | 100000     |    43.695 ns |   125.1510 ns |  6.8600 ns |    39.806 ns |  1.02 |    0.19 |      36 B |        1.00 |
| MultiQueueSticky4_Enqueue_Int        | 100000     |    41.651 ns |   108.7519 ns |  5.9611 ns |    38.278 ns |  0.97 |    0.17 |      52 B |        1.44 |
| Locking_Enqueue_Int                  | 100000     |    22.590 ns |   130.9250 ns |  7.1764 ns |    18.482 ns |  0.52 |    0.16 |         - |        0.00 |
| RawLocked_Enqueue_Int                | 100000     |    22.675 ns |   132.6547 ns |  7.2713 ns |    18.480 ns |  0.53 |    0.16 |         - |        0.00 |
| RawUnlocked_Enqueue_Int              | 100000     |     6.187 ns |   130.3503 ns |  7.1449 ns |     2.064 ns |  0.14 |    0.15 |         - |        0.00 |
| MultiQueue_Enqueue_String            | 100000     |   168.548 ns |   413.2995 ns | 22.6543 ns |   155.751 ns |  3.92 |    0.67 |      36 B |        1.00 |
|                                      |            |              |               |            |              |       |         |           |             |
| **MultiQueue_EnqueueDequeue_Int**        | **1000000**    |   **167.923 ns** |    **18.2295 ns** |  **0.9992 ns** |   **167.456 ns** |  **3.78** |    **0.62** |         **-** |          **NA** |
| MultiQueueSticky4_EnqueueDequeue_Int | 1000000    |   155.154 ns |     6.0629 ns |  0.3323 ns |   155.068 ns |  3.49 |    0.57 |         - |          NA |
| Locking_EnqueueDequeue_Int           | 1000000    |    89.622 ns |     8.4549 ns |  0.4634 ns |    89.691 ns |  2.02 |    0.33 |         - |          NA |
| RawLocked_EnqueueDequeue_Int         | 1000000    |    79.078 ns |    12.6006 ns |  0.6907 ns |    79.038 ns |  1.78 |    0.29 |         - |          NA |
| RawUnlocked_EnqueueDequeue_Int       | 1000000    |    73.037 ns |    13.5207 ns |  0.7411 ns |    72.837 ns |  1.64 |    0.27 |         - |          NA |
| MultiQueue_EnqueueDequeue_String     | 1000000    | 1,306.848 ns | 1,621.8672 ns | 88.9000 ns | 1,339.619 ns | 29.39 |    5.13 |         - |          NA |
| MultiQueue_Enqueue_Int               | 1000000    |    45.685 ns |   177.1330 ns |  9.7093 ns |    40.155 ns |  1.03 |    0.25 |         - |          NA |
| MultiQueueSticky4_Enqueue_Int        | 1000000    |    43.638 ns |   174.9206 ns |  9.5880 ns |    38.170 ns |  0.98 |    0.25 |       1 B |          NA |
| Locking_Enqueue_Int                  | 1000000    |    22.431 ns |   127.5707 ns |  6.9926 ns |    18.400 ns |  0.50 |    0.16 |         - |          NA |
| RawLocked_Enqueue_Int                | 1000000    |    26.569 ns |   130.3363 ns |  7.1442 ns |    22.461 ns |  0.60 |    0.17 |         - |          NA |
| RawUnlocked_Enqueue_Int              | 1000000    |     6.011 ns |   124.5325 ns |  6.8260 ns |     2.074 ns |  0.14 |    0.14 |         - |          NA |
| MultiQueue_Enqueue_String            | 1000000    |   173.748 ns |   487.1838 ns | 26.7042 ns |   159.495 ns |  3.91 |    0.83 |         - |          NA |
