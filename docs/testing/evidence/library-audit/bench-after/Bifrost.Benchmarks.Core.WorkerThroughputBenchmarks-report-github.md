```

BenchmarkDotNet v0.14.0, Fedora Linux 45 (KDE Plasma Desktop Edition)
13th Gen Intel Core i9-13900K, 1 CPU, 32 logical and 24 physical cores
.NET SDK 10.0.303
  [Host]     : .NET 10.0.11 (10.0.1126.37416), X64 RyuJIT AVX2
  Job-CHPZQK : .NET 10.0.11 (10.0.1126.37416), X64 RyuJIT AVX2

InvocationCount=1  IterationCount=10  LaunchCount=2  
UnrollFactor=1  WarmupCount=5  

```
| Method     | WorkerCount | Mean      | Error    | StdDev    | Gen0      | Allocated   |
|----------- |------------ |----------:|---------:|----------:|----------:|------------:|
| **Throughput** | **1**           |  **21.42 ms** | **5.339 ms** |  **6.148 ms** |         **-** |   **238.48 KB** |
| **Throughput** | **4**           |  **71.49 ms** | **5.600 ms** |  **6.224 ms** |         **-** |  **9908.84 KB** |
| **Throughput** | **16**          | **249.07 ms** | **9.351 ms** | **10.769 ms** | **1000.0000** | **26037.92 KB** |
