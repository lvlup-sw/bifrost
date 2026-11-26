```

BenchmarkDotNet v0.14.0, macOS Sonoma 14.4.1 (23E224) [Darwin 23.4.0]
Apple M3, 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.303
  [Host]   : .NET 10.0.13 (10.0.1326.50504), Arm64 RyuJIT AdvSIMD
  ShortRun : .NET 10.0.13 (10.0.1326.50504), Arm64 RyuJIT AdvSIMD

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method               | Mean     | Error     | StdDev    | Gen0   | Allocated |
|--------------------- |---------:|----------:|----------:|-------:|----------:|
| ParkWake_SteadyState | 4.402 μs | 0.5152 μs | 0.0282 μs | 0.0234 |     208 B |
