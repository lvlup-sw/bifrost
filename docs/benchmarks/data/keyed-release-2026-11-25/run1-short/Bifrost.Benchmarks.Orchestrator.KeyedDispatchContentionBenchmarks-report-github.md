```

BenchmarkDotNet v0.14.0, macOS Sonoma 14.4.1 (23E224) [Darwin 23.4.0]
Apple M3, 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.303
  [Host]   : .NET 10.0.13 (10.0.1326.50504), Arm64 RyuJIT AdvSIMD
  ShortRun : .NET 10.0.13 (10.0.1326.50504), Arm64 RyuJIT AdvSIMD

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method                      | Mode      | Mean       | Error       | StdDev     | Median     | Allocated |
|---------------------------- |---------- |-----------:|------------:|-----------:|-----------:|----------:|
| **ContendedEnqueueAndDispatch** | **Fifo**      |   **915.8 ns** |  **9,257.4 ns** |   **507.4 ns** |   **650.5 ns** |         **-** |
| **ContendedEnqueueAndDispatch** | **Keyed1**    | **1,904.3 ns** | **18,552.1 ns** | **1,016.9 ns** | **1,462.3 ns** |      **48 B** |
| **ContendedEnqueueAndDispatch** | **Keyed4096** |   **811.8 ns** |  **1,913.1 ns** |   **104.9 ns** |   **752.5 ns** |     **170 B** |
| **ContendedEnqueueAndDispatch** | **Keyed64**   | **1,019.5 ns** |  **2,863.7 ns** |   **157.0 ns** |   **938.0 ns** |      **46 B** |
