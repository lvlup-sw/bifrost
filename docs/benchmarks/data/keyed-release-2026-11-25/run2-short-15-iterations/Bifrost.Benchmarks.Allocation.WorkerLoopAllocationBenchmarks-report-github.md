```

BenchmarkDotNet v0.14.0, macOS Sonoma 14.4.1 (23E224) [Darwin 23.4.0]
Apple M3, 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.303
  [Host]     : .NET 10.0.13 (10.0.1326.50504), Arm64 RyuJIT AdvSIMD
  Job-BAMWRE : .NET 10.0.13 (10.0.1326.50504), Arm64 RyuJIT AdvSIMD

InvocationCount=1  IterationCount=15  LaunchCount=1  
UnrollFactor=1  WarmupCount=5  

```
| Method                 | Mean     | Error     | StdDev    | Median   | Allocated |
|----------------------- |---------:|----------:|----------:|---------:|----------:|
| WorkerLoop_SteadyState | 9.390 μs | 14.614 μs | 12.955 μs | 1.077 μs |         - |
