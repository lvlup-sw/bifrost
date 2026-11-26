```

BenchmarkDotNet v0.14.0, macOS Sonoma 14.4.1 (23E224) [Darwin 23.4.0]
Apple M3, 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.303
  [Host]     : .NET 10.0.13 (10.0.1326.50504), Arm64 RyuJIT AdvSIMD
  Job-BAMWRE : .NET 10.0.13 (10.0.1326.50504), Arm64 RyuJIT AdvSIMD

InvocationCount=1  IterationCount=15  LaunchCount=1  
UnrollFactor=1  WarmupCount=5  

```
| Method                      | Mode      | Mean     | Error     | StdDev    | Median    | Allocated |
|---------------------------- |---------- |---------:|----------:|----------:|----------:|----------:|
| **ContendedEnqueueAndDispatch** | **Fifo**      | **1.286 μs** | **1.1908 μs** | **1.0556 μs** | **0.7842 μs** |         **-** |
| **ContendedEnqueueAndDispatch** | **Keyed1**    | **1.850 μs** | **0.8153 μs** | **0.7626 μs** | **1.8012 μs** |      **58 B** |
| **ContendedEnqueueAndDispatch** | **Keyed4096** | **1.686 μs** | **1.1684 μs** | **0.9756 μs** | **1.1586 μs** |     **169 B** |
| **ContendedEnqueueAndDispatch** | **Keyed64**   | **4.498 μs** | **3.8330 μs** | **3.3978 μs** | **3.5751 μs** |      **39 B** |
