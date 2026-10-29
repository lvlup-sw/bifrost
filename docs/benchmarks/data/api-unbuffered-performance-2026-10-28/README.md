# Unbuffered reference-stripe investigation evidence

[Analysis and results](../../2026-10-28-api-unbuffered-performance.md).

No queue optimization from this investigation was retained. The production queue matches PR commit `6fb7920`; the follow-up production/build diff is only the SourceLink private build dependency update.

## Contents

- `measurements.json`: mapping from original relative log path to complete stdout, covering screening, confirmation and final preservation runs. Paths retain mode, sequence/block/slot and version. Aggregate summary text is also included where it existed; it is not an additional run.
- `confirm-summary.json`, `preservation-summary.json`: process medians, ranges, sample counts and underlying values. Independent `stripe` outputs are in `measurements.json`; that mode is not parsed by the inherited summary script.
- `experiments.json`: rejected source patches relative to C, plus SHA-256 of each resulting `SubQueue.cs`. `current` is the unchanged control; the other fourteen entries are candidates. These patches are alternatives, not a patch series.
- `source-hashes.json`: exact control source and driver identities. Production `.cs` hashes are asserted equal to C by the archive script.
- `jit/`: fresh FullOpts diagnostics for C, explicit-result and comparer-local variants; normalized comparer-code diff; diagnostic stdout (not included in timing medians).
- `verification.json`: build/test/AOT logs, all trace/quality output and algorithm comparison result. Builds and AOT run with package auditing enabled.
- `scripts/`: experiment generation, build, timing, summary and archival scripts used during the session. They retain the original scratch paths and should be read as the command record. `stripe-harness/` is the independent dense unbuffered reference diagnostic.

The completed screening sequences include both forward and reverse process order, two processes per version. The comparer-local candidate's confirmation contains six full matrices, six sparse runs and six independent stripe runs per relevant version. Its throughput campaign was stopped after these failed acceptance; the three completed buffered-only processes per version remain in the raw data as ancillary evidence. There is no completed `perf` run for that rejected candidate. An initial scratch root-specialization build was corrected before the completed root screening sequence; no failed executable contributes a timing.

## Reproduce the controls

Build from a clean copy of the specified top-level concurrency `.cs` files, without test hooks:

- C (`current`): `src/Bifrost.Concurrency/*.cs` at Git `6fb7920`.
- I (`old`): copy C, then reverse `../api-latency-performance-2026-10-28/optimization.patch` inside that source directory (`patch -R -p1`).
- P (`preprofile`): copy I, then reverse `../api-profile-performance-2026-10-28/optimization.patch` the same way.
- O (`original`): top-level concurrency `.cs` files at Git `6800ae0`.
- L (`comparer-local`): copy C, then apply that entry's `patch` from `experiments.json` inside the source directory (`patch -p3`). Other entries are independent alternatives to C.

Compare the resulting files with `source-hashes.json` before using the controls. The previous report's source reconstruction instructions and patches supply the older controls. The validation driver itself is unchanged in this investigation.

From the repository root, for each control:

```sh
dotnet build tools/queue-validation/Validation.csproj -c Release -p:QueueSources=/absolute/control/sources -o /absolute/control/bin
DOTNET_TieredCompilation=0 dotnet /absolute/control/bin/Validation.dll batch
DOTNET_TieredCompilation=0 dotnet /absolute/control/bin/Validation.dll batch-sparse
# Leave tiering at its default for throughput:
dotnet /absolute/control/bin/Validation.dll
```

Build the independent diagnostic similarly:

```sh
dotnet build docs/benchmarks/data/api-unbuffered-performance-2026-10-28/stripe-harness/Stripe.csproj -c Release -p:QueueSources=/absolute/control/sources -o /absolute/control/stripe
DOTNET_TieredCompilation=0 dotnet /absolute/control/stripe/Stripe.dll reference
```

Use separate processes in the report's palindromic order. Keep all benchmark processes sequential and separate from builds, tests and diagnostics. The target full-matrix line is `payload=reference custom=True population=4096 c=0`; the independent diagnostic is deliberately a distinct context. Do not substitute it for the full matrix or interpret a single run as proof that the regression was removed.

For JIT diagnostics, add `DOTNET_JitDisasm='*TryLockedPop* *MoveDownCustomComparer* *UnbufferedPopHeldRoot*'` and `DOTNET_JitStdOutFile=/absolute/jit.txt` to a separate, tiering-disabled `batch` run. Exclude that run from timing summaries. The driver allocates payloads before timing; recorded `allocated=0` is per measured loop, not a claim that construction, warmup or storage growth never allocates.
