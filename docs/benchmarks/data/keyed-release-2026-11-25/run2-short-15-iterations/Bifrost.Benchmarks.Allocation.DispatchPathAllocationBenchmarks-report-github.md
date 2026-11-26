```

BenchmarkDotNet v0.14.0, macOS Sonoma 14.4.1 (23E224) [Darwin 23.4.0]
Apple M3, 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.303
  [Host]     : .NET 10.0.13 (10.0.1326.50504), Arm64 RyuJIT AdvSIMD
  Job-BAMWRE : .NET 10.0.13 (10.0.1326.50504), Arm64 RyuJIT AdvSIMD

InvocationCount=1  IterationCount=15  LaunchCount=1  
UnrollFactor=1  WarmupCount=5  

```
| Method                         | Strategy      | Mean     | Error    | StdDev   | Median    | Allocated |
|------------------------------- |-------------- |---------:|---------:|---------:|----------:|----------:|
| **EnqueueAndDispatch_SteadyState** | **Fifo**          | **4.417 μs** | **5.876 μs** | **4.588 μs** | **1.9838 μs** |         **-** |
| **EnqueueAndDispatch_SteadyState** | **Keyed**         | **2.516 μs** | **3.040 μs** | **2.695 μs** | **0.8930 μs** |      **40 B** |
| **EnqueueAndDispatch_SteadyState** | **PriorityExact** | **3.235 μs** | **3.885 μs** | **3.444 μs** | **1.4337 μs** |         **-** |
