```

BenchmarkDotNet v0.14.0, macOS Sonoma 14.4.1 (23E224) [Darwin 23.4.0]
Apple M3, 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.303
  [Host]   : .NET 10.0.13 (10.0.1326.50504), Arm64 RyuJIT AdvSIMD
  ShortRun : .NET 10.0.13 (10.0.1326.50504), Arm64 RyuJIT AdvSIMD

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method                         | Strategy      | Mean       | Error      | StdDev   | Allocated |
|------------------------------- |-------------- |-----------:|-----------:|---------:|----------:|
| **EnqueueAndDispatch_SteadyState** | **Fifo**          |   **761.7 ns** | **4,423.7 ns** | **242.5 ns** |         **-** |
| **EnqueueAndDispatch_SteadyState** | **Keyed**         |   **709.0 ns** | **2,482.9 ns** | **136.1 ns** |      **39 B** |
| **EnqueueAndDispatch_SteadyState** | **PriorityExact** | **1,548.8 ns** | **6,129.6 ns** | **336.0 ns** |         **-** |
