```

BenchmarkDotNet v0.14.0, Ubuntu 24.04.4 LTS (Noble Numbat)
INTEL XEON PLATINUM 8573C, 1 CPU, 64 logical and 32 physical cores
.NET SDK 10.0.301
  [Host]   : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  ShortRun : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                   | BufferCapacity | Population | Mean      | Error     | StdDev   | Ratio | RatioSD | Allocated | Alloc Ratio |
|----------------------------------------- |--------------- |----------- |----------:|----------:|---------:|------:|--------:|----------:|------------:|
| **Buffered_EnqueueDequeue_ReferenceElement** | **0**              | **10**         | **145.70 ns** | **11.908 ns** | **0.653 ns** |  **0.97** |    **0.01** |         **-** |          **NA** |
| Buffered_EnqueueDequeue_ValueElement     | 0              | 10         | 150.39 ns | 30.746 ns | 1.685 ns |  1.00 |    0.01 |         - |          NA |
|                                          |                |            |           |           |          |       |         |           |             |
| **Buffered_EnqueueDequeue_ReferenceElement** | **0**              | **1000**       | **103.35 ns** |  **5.030 ns** | **0.276 ns** |  **1.32** |    **0.00** |         **-** |          **NA** |
| Buffered_EnqueueDequeue_ValueElement     | 0              | 1000       |  78.05 ns |  0.645 ns | 0.035 ns |  1.00 |    0.00 |         - |          NA |
|                                          |                |            |           |           |          |       |         |           |             |
| **Buffered_EnqueueDequeue_ReferenceElement** | **0**              | **100000**     | **150.19 ns** |  **5.115 ns** | **0.280 ns** |  **1.25** |    **0.02** |         **-** |          **NA** |
| Buffered_EnqueueDequeue_ValueElement     | 0              | 100000     | 119.90 ns | 35.118 ns | 1.925 ns |  1.00 |    0.02 |         - |          NA |
|                                          |                |            |           |           |          |       |         |           |             |
| **Buffered_EnqueueDequeue_ReferenceElement** | **0**              | **1000000**    | **178.59 ns** | **12.428 ns** | **0.681 ns** |  **1.03** |    **0.01** |         **-** |          **NA** |
| Buffered_EnqueueDequeue_ValueElement     | 0              | 1000000    | 174.15 ns | 32.787 ns | 1.797 ns |  1.00 |    0.01 |         - |          NA |
|                                          |                |            |           |           |          |       |         |           |             |
| **Buffered_EnqueueDequeue_ReferenceElement** | **16**             | **10**         | **147.39 ns** | **18.897 ns** | **1.036 ns** |  **0.99** |    **0.01** |         **-** |          **NA** |
| Buffered_EnqueueDequeue_ValueElement     | 16             | 10         | 148.46 ns |  1.559 ns | 0.085 ns |  1.00 |    0.00 |         - |          NA |
|                                          |                |            |           |           |          |       |         |           |             |
| **Buffered_EnqueueDequeue_ReferenceElement** | **16**             | **1000**       | **117.23 ns** |  **5.416 ns** | **0.297 ns** |  **1.43** |    **0.00** |         **-** |          **NA** |
| Buffered_EnqueueDequeue_ValueElement     | 16             | 1000       |  81.79 ns |  1.987 ns | 0.109 ns |  1.00 |    0.00 |         - |          NA |
|                                          |                |            |           |           |          |       |         |           |             |
| **Buffered_EnqueueDequeue_ReferenceElement** | **16**             | **100000**     | **121.55 ns** | **13.899 ns** | **0.762 ns** |  **0.93** |    **0.01** |         **-** |          **NA** |
| Buffered_EnqueueDequeue_ValueElement     | 16             | 100000     | 130.22 ns |  8.462 ns | 0.464 ns |  1.00 |    0.00 |         - |          NA |
|                                          |                |            |           |           |          |       |         |           |             |
| **Buffered_EnqueueDequeue_ReferenceElement** | **16**             | **1000000**    | **122.43 ns** | **22.196 ns** | **1.217 ns** |  **0.68** |    **0.01** |         **-** |          **NA** |
| Buffered_EnqueueDequeue_ValueElement     | 16             | 1000000    | 179.59 ns | 12.515 ns | 0.686 ns |  1.00 |    0.00 |         - |          NA |
