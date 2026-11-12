```

BenchmarkDotNet v0.14.0, Fedora Linux 45 (KDE Plasma Desktop Edition)
13th Gen Intel Core i9-13900K, 1 CPU, 32 logical and 24 physical cores
.NET SDK 10.0.303
  [Host]     : .NET 10.0.11 (10.0.1126.37416), X64 RyuJIT AVX2
  Job-ZIPPMF : .NET 10.0.11 (10.0.1126.37416), X64 RyuJIT AVX2

InvocationCount=1  IterationCount=10  LaunchCount=2
UnrollFactor=1  WarmupCount=5

```
| Method     | WorkerCount | Mean      | Error     | StdDev    | Gen0      | Allocated   |
|----------- |------------ |----------:|----------:|----------:|----------:|------------:|
| **Throughput** | **1**           |  **21.70 ms** |  **1.724 ms** |  **1.916 ms** |         **-** |     **9.49 KB** |
| **Throughput** | **4**           |  **64.65 ms** |  **6.743 ms** |  **7.765 ms** |         **-** |   **5770.9 KB** |
| **Throughput** | **16**          | **265.43 ms** | **18.269 ms** | **21.038 ms** | **1000.0000** | **28871.28 KB** |
