```

BenchmarkDotNet v0.14.0, macOS Sonoma 14.4.1 (23E224) [Darwin 23.4.0]
Apple M3, 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.303
  [Host]   : .NET 10.0.13 (10.0.1326.50504), Arm64 RyuJIT AdvSIMD
  ShortRun : .NET 10.0.13 (10.0.1326.50504), Arm64 RyuJIT AdvSIMD

Job=ShortRun  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method                 | Mean     | Error      | StdDev   | Median    | Allocated |
|----------------------- |---------:|-----------:|---------:|----------:|----------:|
| WorkerLoop_SteadyState | 5.384 μs | 155.483 μs | 8.523 μs | 0.6489 μs |         - |
