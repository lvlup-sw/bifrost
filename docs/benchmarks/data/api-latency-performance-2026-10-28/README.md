# Isolated latency follow-up evidence

See the [report](../../2026-10-28-api-latency-performance.md) and the existing [timing](../../../../tools/queue-validation/README.md) and [profiling](../../../../tools/queue-profile/README.md) drivers.

## Sources and reconstruction

Use temporary copies of `src/Bifrost.Concurrency/*.cs`:

- **F / final**: retained source after this investigation.
- **C / current**: reverse-apply `optimization.patch` with `patch -R -p1` inside a copy of F.
- **P / preprofile**: from C, reverse-apply `../api-profile-performance-2026-10-28/optimization.patch`.
- **O / original**: `src/Bifrost.Concurrency` from Git `6800ae0`.

`source-sha256.json` fingerprints every source version and the unchanged validation/profile harnesses. The new stripe-only diagnostic has its own fingerprint. The retained patch changes only the `PopHeldRoot` inlining annotation and the `ThreadHandle.Current` accessor/cold initialization helper. Apply each experimental patch under `patches/` independently to C; they are not cumulative.

## Files and completed sequences

- `screen/`: eager/partly eager guards and partly eager pop inlining. Three O/P/C/all/partial/both/inline/inline/both/partial/all/C/P/O isolated blocks.
- `outline-screen/`: three C/outline/outline-inline/outline-partial/inline/inline/outline-partial/outline-inline/outline/C isolated blocks.
- `throughput-screen/`: C/outline/outline-partial/inline/inline/outline-partial/outline/C buffered-only and full-throughput sequences.
- `cache-screen/`: cached push-transaction predicate and original-guard pop inlining. Three isolated blocks and one buffered-only sequence, in C/plain-inline/pushcache/pushcache-inline/pushcache-inline/pushcache/plain-inline/C order. The cached-state changes are rejected.
- `tls-screen/`: three isolated blocks and one buffered-only sequence in C/tls-inline/tls-split/tls-pop/tls-pop/tls-split/tls-inline/C order. `tls-pop` is the retained runtime configuration; final-source comments were added before the final build and tests.
- `confirm/`: three complete O/P/C/F/F/C/P/O isolated blocks; two P/C/F/F/C/P buffered-only blocks; two such full-throughput blocks; and C/F/F/C for the full batch matrix. These provide the report tables. There are 24, 12, 12 and 4 completed processes respectively. All runs, including noisy controls, are retained.
- `stripe-diagnostic/`: separate value-only and reference-only processes containing **only** stripe work. Each payload has three C/F/F/C blocks. This removes the preceding/interleaved whole-queue work; do not compare its absolute numbers directly with the original harness.
- `stripe-harness/`: exact dependency-free source/project for that independent diagnostic. It uses the same custom comparer, seed, keys, capacity, population, batch size and warmup count as the original stripe loop.
- `profiles/`: four current/final value/reference native captures, parsed summaries, JIT-address maps, collector logs and compressed EventPipe rundown files. No profile timing supplies a benchmark result.
- `jit/`: all assembly diagnostics, including current/final dumps showing accessor and dispatcher inlining.
- `verification/`: build, test, differential trace, rank/delay, AOT and reproduction checks.
- `scripts/`: exact experimental build/screen scripts and reusable confirmation, summarization, native collection and stripe-diagnostic runners. Historical source-copy paths remain in exploratory builders; the final runners accept a benchmark-root argument.

Each summary contains individual process medians, counts and ranges. They are descriptive summaries, not confidence intervals. Do not discard the slow control runs to produce more favorable ratios.

## Reproduce timing

Build all versions before measuring, with the same current `tools/queue-validation/Program.cs`. For example:

```sh
dotnet build tools/queue-validation/Validation.csproj -c Release \
  -p:QueueSources=/absolute/C-source-copy -o /absolute/bench/current
```

Repeat into `original/`, `preprofile/` and `final/` using O, P and F. Then, from this evidence directory:

```sh
python3 scripts/confirm.py /absolute/bench
python3 scripts/summarize.py /absolute/bench/confirm
```

The runner sets tiering by mode. Use a fresh directory because matching output files are overwritten. Run processes sequentially, without builds, tests or profiling alongside timing.

For the independent stripe check, build both source versions with the supplied project:

```sh
dotnet build stripe-harness/Stripe.csproj -c Release \
  -p:QueueSources=/absolute/C-source-copy -o /absolute/bench/stripe-current
dotnet build stripe-harness/Stripe.csproj -c Release \
  -p:QueueSources=/absolute/F-source-copy -o /absolute/bench/stripe-final
python3 scripts/stripe.py /absolute/bench
```

## Reproduce native/JIT diagnostics

Build the existing `tools/queue-profile/Profile.csproj` against C and F into `profile-current/` and `profile-final/`. Set up dotnet-trace 10.0.750501 and the method-map reader using the [preceding evidence instructions](../api-profile-performance-2026-10-28/README.md#native-profiling-on-macos). The diagnostic-tool root must contain `tools/dotnet-trace` and `reader/bin/Release/net10.0/Reader.dll`.

```sh
python3 scripts/native.py /absolute/bench /absolute/diagnostic-tool-root
python3 ../api-profile-performance-2026-10-28/scripts/analyze-native.py /absolute/bench
```

The collector samples its own children for eight seconds, then attaches EventPipe for method rundown; all retained children exited successfully. The parser can also run directly against `profiles/` using its stored text captures and JSON maps. Decompress the `.nettrace.gz` files if the maps need regenerating.

`scripts/build-diagnostics.py` records the current/final JIT disassembly settings. It expects C source in `before/`, F in the repository, and the validation binaries in `current/` and `final/`. Run it only outside timing. Inlining removes standalone frames but does not remove the TLS read or transaction checks; native sample percentages must not be treated as a precise sum of saved costs.
