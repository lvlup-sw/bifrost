```

BenchmarkDotNet v0.14.0, Pop!_OS 24.04 LTS
13th Gen Intel Core i9-13900K, 1 CPU, 32 logical and 24 physical cores
.NET SDK 10.0.202
  [Host]   : .NET 10.0.6 (10.0.626.17701), X64 RyuJIT AVX2
  ShortRun : .NET 10.0.6 (10.0.626.17701), X64 RyuJIT AVX2

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method                   | WorkerCount | Mean      | Error     | StdDev    | Allocated  |
|------------------------- |------------ |----------:|----------:|----------:|-----------:|
| **EnqueueDispatchRoundTrip** | **1**           |  **4.549 ms** |  **3.218 ms** | **0.1764 ms** |  **568.66 KB** |
| **EnqueueDispatchRoundTrip** | **2**           |  **5.961 ms** |  **1.308 ms** | **0.0717 ms** |  **610.91 KB** |
| **EnqueueDispatchRoundTrip** | **8**           | **19.632 ms** | **15.578 ms** | **0.8539 ms** | **3231.84 KB** |
