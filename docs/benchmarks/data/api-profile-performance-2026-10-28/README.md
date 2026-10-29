# Profiling evidence and reproduction

Later-source note: reverse the [isolated-latency follow-up patch](../api-latency-performance-2026-10-28/optimization.patch) in a temporary source copy before reconstructing this record’s older versions.

See the [report](../../2026-10-28-api-profile-performance.md), [profiling driver](../../../../tools/queue-profile/README.md), and [timing harness](../../../../tools/queue-validation/README.md).

## Contents

- `profiles/`: nine corrected native captures, JIT-address maps, parsed sample summaries, collector/process logs, and compressed EventPipe rundown files. The initial driver with the wrong constructor overload is excluded entirely. All retained children completed successfully.
- `jit/`: original/earlier/current assembly diagnostics, buffer-copy candidate/final assembly, and the address-normalized equivalence check. These are diagnostic executions, not timing runs. The throughput loop contains inlined queue operations.
- `screen/`: broad scalar-copy and one-entry-copy experiments, each in C/loops/one/one/loops/C order for isolated batches and buffered-only throughput.
- `screen-refine/`: removal-loop, guard-order and combined candidates in C/poploop/guardfirst/combined/combined/guardfirst/poploop/C order.
- `confirm/`: complete O/E/C/N/N/C/E/O confirmation of the exploratory combination. Three isolated blocks; one buffered-only block; full throughput and batch matrices. N is `poploop-guard`, before the final eight-byte cutoff.
- `final-confirm/`: final-source O/C/F/F/C/O isolated blocks (three), E/C/F/F/C/E buffered-only and full-throughput runs, and C/F/F/C full-batch runs. These supply the report's final tables.
- `verification/`: Release build, 1,224-test, trace, quality, Native AOT and harness build logs.
- `patches/`: each exploratory candidate relative to C. They are independent patches, not a sequence to apply.
- `optimization.patch`: retained runtime change plus the buffer-helper documentation correction, relative to C.
- `source-sha256.json`: all source versions and shared harness hashes. `timed-final` identifies the benchmark/test source. `final` differs from it only in the subsequent XML documentation correction in `SubQueueBuffer.cs`; runtime code is identical.
- `scripts/` and `reader/`: runners, native-tree parser and EventPipe method-map reader. Exploratory build/screen scripts preserve the exact historical temporary paths; final runners, JIT runners, native collector and parser accept a benchmark-root argument.

`summary.json` in each confirmation directory includes every process median, sample count, minimum and maximum. These are not statistical confidence intervals. `stripeMedianNs` is the internal single-stripe proxy; `pairMedianNs` is the whole-queue pair latency. Neither measures production tail latency under contention.

## Reconstruct sources

Work only in temporary copies of `src/Bifrost.Concurrency/*.cs`:

1. F is the retained source. Reverse-apply `optimization.patch` with `patch -R -p1` inside a copy to reconstruct C.
2. From C, reverse-apply `../api-targeted-performance-2026-10-28/optimization.patch` to reconstruct E.
3. O is `src/Bifrost.Concurrency` from Git `6800ae0`.
4. Apply an exploratory patch to a fresh C copy to reconstruct that candidate. N uses `patches/poploop-guard.patch`.

The eight-byte cutoff leaves measured `int/int` and `object/int` buffer-pop instructions unchanged from N after relocations are normalized. It deliberately keeps larger unmanaged entries on the bulk-copy path. Final timing was repeated rather than inferred from that assembly comparison.

## Unprofiled benchmarks

Choose a fresh benchmark directory. Build all controls with the **same** current `tools/queue-validation/Program.cs`:

```sh
dotnet build tools/queue-validation/Validation.csproj -c Release \
  -p:QueueSources=/absolute/source-copy -o /absolute/bench/validation-current
```

Repeat for `validation-original`, `validation-earlier` and `validation-final`, using their respective source copies. Then:

```sh
python3 scripts/run-final.py /absolute/bench
python3 scripts/summarize.py /absolute/bench/final-confirm
```

`scripts/confirm.py /absolute/bench` reproduces the exploratory confirmation when `validation-poploop-guard` is also built. Runners overwrite matching output filenames, so do not reuse an evidence directory. They set tiering appropriately. Do not run builds, tests, profiling or another timing process alongside them.

## Native profiling on macOS

Build `tools/queue-profile/Profile.csproj` for each desired source copy into `/absolute/bench/VERSION`, using `QueueSources` as above. Install the diagnostic tool locally and build the reader against its TraceEvent assembly; this adds no library or profiling dependency to Bifrost:

```sh
dotnet tool install dotnet-trace --version 10.0.750501 --tool-path /absolute/bench/tools
dotnet build reader/Reader.csproj -c Release \
  -p:TraceEventAssembly=/absolute/bench/tools/.store/dotnet-trace/10.0.750501/dotnet-trace/10.0.750501/tools/net8.0/any/Microsoft.Diagnostics.Tracing.TraceEvent.dll \
  -o /absolute/bench/reader/bin/Release/net10.0
python3 scripts/collect-native.py /absolute/bench \
  current:batch-value original:batch-value \
  current:batch-reference original:batch-reference \
  current:throughput-sparse earlier:throughput-sparse \
  current:throughput-dense earlier:throughput-dense final:throughput-dense
python3 scripts/analyze-native.py /absolute/bench
```

The collector launches its own children, samples for eight seconds, then attaches the method-map collector for two seconds. It checks process exit codes. Native sampling may require process-inspection permission. The throughput start delays match this driver's warmup sequence on the measured host; adjust for slower setup on another host, and continue selecting only `ThroughputTarget` stacks. See the driver README for lifetime requirements.

To reanalyze the archived captures directly, pass `profiles/` as the parser's first argument. It needs only the `.txt` native captures and `.json` method maps. The `.nettrace.gz` files preserve the underlying rundown evidence if the maps need rebuilding. Parsed percentages describe sampled stack occupancy, not allocation, lock-hold time, instruction counts or a precise decomposition of a one-percent regression.
