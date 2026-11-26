```

BenchmarkDotNet v0.14.0, macOS Sonoma 14.4.1 (23E224) [Darwin 23.4.0]
Apple M3, 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.303
  [Host]   : .NET 10.0.13 (10.0.1326.50504), Arm64 RyuJIT AdvSIMD
  ShortRun : .NET 10.0.13 (10.0.1326.50504), Arm64 RyuJIT AdvSIMD

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                 | Mean     | Error    | StdDev   | Ratio | RatioSD | Allocated | Alloc Ratio |
|----------------------- |---------:|---------:|---------:|------:|--------:|----------:|------------:|
| TryEnqueue_ZeroAlloc   | 103.3 ns | 233.4 ns | 12.79 ns |  1.01 |    0.16 |         - |          NA |
| EnqueueAsync_ZeroAlloc | 289.8 ns | 385.1 ns | 21.11 ns |  2.84 |    0.37 |         - |          NA |
