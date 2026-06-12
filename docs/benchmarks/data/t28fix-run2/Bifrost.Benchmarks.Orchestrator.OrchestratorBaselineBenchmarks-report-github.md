```

BenchmarkDotNet v0.14.0, Pop!_OS 24.04 LTS
13th Gen Intel Core i9-13900K, 1 CPU, 32 logical and 24 physical cores
.NET SDK 10.0.202
  [Host]    : .NET 10.0.6 (10.0.626.17701), X64 RyuJIT AVX2
  MediumRun : .NET 10.0.6 (10.0.626.17701), X64 RyuJIT AVX2

Job=MediumRun  InvocationCount=1  IterationCount=15  
LaunchCount=2  UnrollFactor=1  WarmupCount=10  

```
| Method                   | WorkerCount | Mean      | Error     | StdDev    | Allocated |
|------------------------- |------------ |----------:|----------:|----------:|----------:|
| **EnqueueDispatchRoundTrip** | **1**           |  **2.774 ms** | **0.1187 ms** | **0.1664 ms** |         **-** |
| **EnqueueDispatchRoundTrip** | **2**           |  **3.997 ms** | **0.2054 ms** | **0.3074 ms** |  **148000 B** |
| **EnqueueDispatchRoundTrip** | **8**           | **15.219 ms** | **1.8708 ms** | **2.6831 ms** | **1292416 B** |
