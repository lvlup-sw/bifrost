```

BenchmarkDotNet v0.14.0, Pop!_OS 24.04 LTS
13th Gen Intel Core i9-13900K, 1 CPU, 32 logical and 24 physical cores
.NET SDK 10.0.202
  [Host]    : .NET 10.0.6 (10.0.626.17701), X64 RyuJIT AVX2
  MediumRun : .NET 10.0.6 (10.0.626.17701), X64 RyuJIT AVX2

Job=MediumRun  InvocationCount=1  IterationCount=15  
LaunchCount=2  UnrollFactor=1  WarmupCount=10  

```
| Method                   | WorkerCount | Mean      | Error     | StdDev    | Allocated  |
|------------------------- |------------ |----------:|----------:|----------:|-----------:|
| **EnqueueDispatchRoundTrip** | **1**           |  **4.127 ms** | **0.3108 ms** | **0.4651 ms** |  **103.53 KB** |
| **EnqueueDispatchRoundTrip** | **2**           |  **5.039 ms** | **0.1658 ms** | **0.2482 ms** |  **553.34 KB** |
| **EnqueueDispatchRoundTrip** | **8**           | **20.067 ms** | **0.9154 ms** | **1.3701 ms** | **4477.03 KB** |
