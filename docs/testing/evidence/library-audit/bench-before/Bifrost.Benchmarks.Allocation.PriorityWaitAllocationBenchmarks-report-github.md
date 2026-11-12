```

BenchmarkDotNet v0.14.0, Fedora Linux 45 (KDE Plasma Desktop Edition)
13th Gen Intel Core i9-13900K, 1 CPU, 32 logical and 24 physical cores
.NET SDK 10.0.303
  [Host]     : .NET 10.0.11 (10.0.1126.37416), X64 RyuJIT AVX2
  Job-KWQLTE : .NET 10.0.11 (10.0.1126.37416), X64 RyuJIT AVX2

IterationCount=10  LaunchCount=2  WarmupCount=5  

```
| Method               | Mean     | Error    | StdDev   | Median   | Gen0   | Allocated |
|--------------------- |---------:|---------:|---------:|---------:|-------:|----------:|
| ParkWake_SteadyState | 3.475 μs | 1.149 μs | 1.277 μs | 2.561 μs | 0.0078 |     208 B |
