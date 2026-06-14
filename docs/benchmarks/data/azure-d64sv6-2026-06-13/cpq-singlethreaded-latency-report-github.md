```

BenchmarkDotNet v0.14.0, Ubuntu 24.04.4 LTS (Noble Numbat)
INTEL XEON PLATINUM 8573C, 1 CPU, 64 logical and 32 physical cores
.NET SDK 10.0.301
  [Host]   : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  ShortRun : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                           | Population | Mean        | Error        | StdDev    | Allocated |
|--------------------------------- |----------- |------------:|-------------:|----------:|----------:|
| **MultiQueue_EnqueueDequeue_Int**    | **10**         |   **259.73 ns** |    **22.693 ns** |  **1.244 ns** |         **-** |
| Locking_EnqueueDequeue_Int       | 10         |    37.97 ns |     0.577 ns |  0.032 ns |         - |
| RawLocked_EnqueueDequeue_Int     | 10         |    20.32 ns |     0.098 ns |  0.005 ns |         - |
| **MultiQueue_EnqueueDequeue_Int**    | **1000**       |    **80.05 ns** |     **1.179 ns** |  **0.065 ns** |         **-** |
| Locking_EnqueueDequeue_Int       | 1000       |    62.52 ns |     2.797 ns |  0.153 ns |         - |
| RawLocked_EnqueueDequeue_Int     | 1000       |    57.49 ns |     3.804 ns |  0.209 ns |         - |
| **MultiQueue_EnqueueDequeue_Int**    | **100000**     |   **126.02 ns** |     **1.743 ns** |  **0.096 ns** |         **-** |
| Locking_EnqueueDequeue_Int       | 100000     |    78.98 ns |     5.425 ns |  0.297 ns |         - |
| RawLocked_EnqueueDequeue_Int     | 100000     |    68.74 ns |     4.260 ns |  0.234 ns |         - |
| **MultiQueue_EnqueueDequeue_Int**    | **1000000**    |   **175.91 ns** |    **40.254 ns** |  **2.206 ns** |         **-** |
| Locking_EnqueueDequeue_Int       | 1000000    |    89.45 ns |     5.856 ns |  0.321 ns |         - |
| RawLocked_EnqueueDequeue_Int     | 1000000    |    79.75 ns |     2.395 ns |  0.131 ns |         - |
| **MultiQueue_EnqueueDequeue_String** | **10**         |   **364.59 ns** |     **6.883 ns** |  **0.377 ns** |         **-** |
| **MultiQueue_EnqueueDequeue_String** | **1000**       |   **359.09 ns** |    **12.718 ns** |  **0.697 ns** |         **-** |
| **MultiQueue_EnqueueDequeue_String** | **100000**     | **1,037.71 ns** |   **114.442 ns** |  **6.273 ns** |         **-** |
| **MultiQueue_EnqueueDequeue_String** | **1000000**    | **1,322.55 ns** | **1,606.461 ns** | **88.056 ns** |         **-** |
