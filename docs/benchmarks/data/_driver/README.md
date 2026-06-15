# Throughput driver (DR-7 before/after)

Standalone driver used for the dense / drain throughput runs in
`2026-06-14-cpq-bitmask-before-after.md`. It is **not** part of the benchmark
project — it is a throwaway `.csproj` that `<Reference>`s the already-built
`Bifrost.Concurrency.dll` and re-uses the harness files
(`ThroughputRunner.cs`, `ThroughputModel.cs`, `IThroughputQueue.cs`, copied
verbatim from `src/Bifrost.Benchmarks/Concurrency/`, which are byte-identical on
`main` and the feature branch).

Why not the `throughput` verb? The verb runs the *full* sweep (3 targets × 4
workloads × the 1..2×cores ladder); this driver restricts to
`MultiQueueRelaxed × {UniformMixed5050, NarrowKeyRange, Drain}` at threads
`{4, 16, 32}` so a before/after pass fits the time box.

## Reproduce

```bash
# per branch:
dotnet build src/Bifrost.Concurrency/Bifrost.Concurrency.csproj -c Release
# point driver.csproj HintPath at .../Bifrost.Concurrency/bin/Release/net10.0/Bifrost.Concurrency.dll
dotnet build -c Release            # in the driver dir
dotnet bin/Release/net10.0/cpqdriver.dll <label> <windowSeconds> <trials>
```

The single-thread latency numbers come from the **production** BDN benchmark
(`CpqSingleThreadedLatencyBenchmarks.MultiQueue_EnqueueDequeue_Int`, `--job Short`),
not this driver.
