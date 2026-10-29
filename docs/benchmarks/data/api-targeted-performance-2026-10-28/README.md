# Targeted performance evidence

Later-source note: reverse the [isolated-latency follow-up patch](../api-latency-performance-2026-10-28/optimization.patch) in a temporary source copy before reconstructing this record’s older versions.

Reconstruction note: if starting from the later profiling optimization, first reverse-apply [that patch](../api-profile-performance-2026-10-28/optimization.patch) inside a temporary source copy before following this record’s older reconstruction steps.

See the [analysis and results](../../2026-10-28-api-targeted-performance.md) and [harness instructions](../../../../tools/queue-validation/README.md).

## Final comparisons

`final-MODE-BLOCK-SLOT-VERSION.txt` records every final measurement process. Slots run `original/prior/final/final/prior/original`; there is one block for each full matrix and three blocks for each isolated target. All 48 processes completed successfully. `batch` and `batch-sparse` disable tiered compilation; `perf` and `dense` use normal tiering.

- `original`: Git `6800ae0`, without the new comparer-exception guarantee.
- `prior`: the preceding optimized safe version, with the reported 7%/1.7% gaps.
- `final`: the current comparison-free-refill optimization and numeric size-write simplification.

`source-sha256.json` fingerprints all compiled queue source files and the harness. `optimization.patch` isolates this investigation's production changes. Reverse-apply it with `patch -R -p1` to a temporary copy of the current queue source to reconstruct `prior`. To reconstruct the earlier safe version, reverse-apply the previous report's patch afterward. Both reconstructions were verified byte-for-byte against their saved source snapshots.

Build the three executables before timing. `python3 run-final.py /path/to/benchmark-directory` expects `original/Validation.dll`, `prior/Validation.dll` and `final/Validation.dll` there, and writes all raw files into that directory. It overwrites identically named measurement files, so use a fresh directory for new runs. `python3 summarize.py /path/to/benchmark-directory` writes `summary.json` and prints the table inputs; redirect stdout to preserve a text summary. Means and ranges are descriptive aggregates of process-level medians, not confidence intervals.

## Exploratory comparisons

These files are retained separately and are not pooled into the final tables:

- `sparse-candidate`: comparison-free single-entry refill only.
- `sparse-c2`: also reordered guards and removed the redundant numeric size store.
- `sparse-c3*`, `sparse-c4*`: discarded deletion-buffer span variants.
- `sparse-c5*`: retained variant using the existing locked stripe count.
- `sparse-prior*`, `sparse-original`: contemporaneous controls.
- `dense-BLOCK-SLOT-*.txt`: preliminary three-block O/P/C2/C2/P/O repeatability check.
- `c6-check-*`: final-versus-classifier-first guard experiment, F/C/C/F for isolated sparse, full batch and full throughput. `c6-guard-order.patch` records that discarded candidate relative to final. The runner scripts are included; pass the benchmark-directory path when reproducing.

## Verification

`verification/` contains the Release build, all three test suites, matching trace/quality output and Native AOT logs. The first sandboxed AOT publish stalled and was stopped; `aot-publish-retry.txt` and `aot-run.txt` are the successful single-node retry and execution. The local SourceLink dependency audit workaround is described in the report.
