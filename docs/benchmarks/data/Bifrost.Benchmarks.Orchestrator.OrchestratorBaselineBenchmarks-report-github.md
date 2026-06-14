```

BenchmarkDotNet v0.14.0, Pop!_OS 24.04 LTS
13th Gen Intel Core i9-13900K, 1 CPU, 32 logical and 24 physical cores
.NET SDK 10.0.202
  [Host]   : .NET 10.0.6 (10.0.626.17701), X64 RyuJIT AVX2
  ShortRun : .NET 10.0.6 (10.0.626.17701), X64 RyuJIT AVX2

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method                   | WorkerCount | Mean      | Error    | StdDev    | Allocated |
|------------------------- |------------ |----------:|---------:|----------:|----------:|
| **EnqueueDispatchRoundTrip** | **1**           |  **2.109 ms** | **1.671 ms** | **0.0916 ms** |         **-** |
| **EnqueueDispatchRoundTrip** | **2**           |  **3.130 ms** | **7.114 ms** | **0.3899 ms** |   **36336 B** |
| **EnqueueDispatchRoundTrip** | **8**           | **10.342 ms** | **5.492 ms** | **0.3010 ms** |  **703264 B** |
