```

BenchmarkDotNet v0.14.0, Pop!_OS 24.04 LTS
13th Gen Intel Core i9-13900K, 1 CPU, 32 logical and 24 physical cores
.NET SDK 10.0.202
  [Host]   : .NET 10.0.6 (10.0.626.17701), X64 RyuJIT AVX2
  ShortRun : .NET 10.0.6 (10.0.626.17701), X64 RyuJIT AVX2

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method                   | WorkerCount | Mean      | Error     | StdDev    | Allocated |
|------------------------- |------------ |----------:|----------:|----------:|----------:|
| **EnqueueDispatchRoundTrip** | **1**           |  **2.956 ms** | **1.0342 ms** | **0.0567 ms** |         **-** |
| **EnqueueDispatchRoundTrip** | **2**           |  **3.135 ms** | **0.7674 ms** | **0.0421 ms** |   **68032 B** |
| **EnqueueDispatchRoundTrip** | **8**           | **12.275 ms** | **7.4223 ms** | **0.4068 ms** |  **759712 B** |
