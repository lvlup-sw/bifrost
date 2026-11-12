```

BenchmarkDotNet v0.14.0, Fedora Linux 45 (KDE Plasma Desktop Edition)
13th Gen Intel Core i9-13900K, 1 CPU, 32 logical and 24 physical cores
.NET SDK 10.0.303
  [Host]     : .NET 10.0.11 (10.0.1126.37416), X64 RyuJIT AVX2
  Job-CHPZQK : .NET 10.0.11 (10.0.1126.37416), X64 RyuJIT AVX2

InvocationCount=1  IterationCount=10  LaunchCount=2  
UnrollFactor=1  WarmupCount=5  

```
| Method                 | Mean     | Error    | StdDev   | Allocated |
|----------------------- |---------:|---------:|---------:|----------:|
| WorkerLoop_SteadyState | 256.6 ns | 16.15 ns | 15.86 ns |         - |
