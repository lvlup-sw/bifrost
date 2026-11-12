```

BenchmarkDotNet v0.14.0, Fedora Linux 45 (KDE Plasma Desktop Edition)
13th Gen Intel Core i9-13900K, 1 CPU, 32 logical and 24 physical cores
.NET SDK 10.0.303
  [Host]     : .NET 10.0.11 (10.0.1126.37416), X64 RyuJIT AVX2
  Job-YXHOIM : .NET 10.0.11 (10.0.1126.37416), X64 RyuJIT AVX2

IterationCount=10  LaunchCount=2  WarmupCount=5

```
| Method               | Mean     | Error    | StdDev   | Median   | Gen0   | Allocated |
|--------------------- |---------:|---------:|---------:|---------:|-------:|----------:|
| ParkWake_SteadyState | 3.780 μs | 1.038 μs | 1.066 μs | 4.744 μs | 0.0098 |     208 B |
