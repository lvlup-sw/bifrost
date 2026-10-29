# MultiQueue profiling driver

This diagnostic executable compiles queue source directly, without test hooks. It extends the validation harness's hot loops long enough to sample them; its elapsed times are **not benchmark results**. Use `tools/queue-validation` for unprofiled comparisons.

```sh
dotnet build tools/queue-profile/Profile.csproj -c Release -o /tmp/queue-profile
DOTNET_TieredCompilation=0 dotnet /tmp/queue-profile/Profile.dll batch-value 20
DOTNET_TieredCompilation=0 dotnet /tmp/queue-profile/Profile.dll batch-reference 20
dotnet /tmp/queue-profile/Profile.dll throughput-sparse 8
dotnet /tmp/queue-profile/Profile.dll throughput-dense 8
```

Pass `-p:QueueSources=/absolute/source/directory` when building another version. Always rebuild controls with the same harness. Queue constructors deliberately use named arguments: positional integer arguments can select the bounded-capacity overload.

The batch modes alternate whole-queue and single-stripe operations, using preallocated integer or reference payloads, a custom integer comparer, 32 stripes, population 16 and buffer capacity 16. Select stacks beneath `QueueBatch` to exclude the stripe control. Disable tiering for these modes, as in the validation harness.

Throughput modes retain the validation matrix's preceding workloads and one-second warmups, then run three measured-workload intervals of the requested duration. Use normal tiering. Select stacks beneath `ThroughputTarget`, excluding `ThroughputWarmup`. The target begins after approximately 9 seconds for sparse and 25 seconds for dense; allow extra time for setup. `ThroughputLoop` includes inlined queue operations, so samples attributed to that method are not all harness overhead.

On macOS, sample the process with `sample PID 8 1 -file samples.txt`. After sampling has finished, attach `dotnet-trace collect --profile dotnet-common --process-id PID --duration 00:00:02 --output methods.nettrace` to obtain method-address rundown. Keep the process alive through both steps. Mapping sampled PCs into rundown method ranges supplies JIT method names; inline frames and some runtime stubs remain unresolved. Capturing the map after native sampling avoids perturbing those samples with EventPipe collection.

Do not run builds, tests or other measurements alongside profiling. Neither sample percentages nor instrumented elapsed times establish a one-percent performance difference. Confirm candidates with repeated, balanced, unprofiled runs.

See the [profiling report and evidence](../../docs/benchmarks/2026-10-28-api-profile-performance.md) for the collected profiles, JIT mapping reader, analysis scripts, candidate results and remaining tradeoffs.

The [isolated-latency follow-up](../../docs/benchmarks/2026-10-28-api-latency-performance.md) uses this same driver to confirm accessor/dispatcher inlining. Its evidence also includes an independent stripe-only timing diagnostic, separate from these profiles and the original validation harness.
