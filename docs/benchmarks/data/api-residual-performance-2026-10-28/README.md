# Residual performance evidence

Later-source note: reverse the [isolated-latency follow-up patch](../api-latency-performance-2026-10-28/optimization.patch) in a temporary source copy before reconstructing this record’s older versions.

Reconstruction note: if starting from the later profiling optimization, first reverse-apply [that patch](../api-profile-performance-2026-10-28/optimization.patch) inside a temporary source copy before following this record’s older reconstruction steps.

See the [report](../../2026-10-28-api-residual-performance.md) and [harness instructions](../../../../tools/queue-validation/README.md). No production queue change was retained in this investigation.

## Retained-version matrices

Root `retained-MODE-BLOCK-SLOT-VERSION.txt` files contain all twelve completed final full-matrix processes. The order is original/earlier/current/current/earlier/original for each mode. `summary.json` and `summary.txt` aggregate the process medians; they do not represent confidence intervals.

- `original`: Git `6800ae0`, before the new exception guarantee.
- `earlier`: safe version before the preceding targeted optimization. Reconstruct by copying current queue source to a temporary directory and reverse-applying `../api-targeted-performance-2026-10-28/optimization.patch` with `patch -R -p1` inside that copy.
- `current`: source at both the start and end of this investigation. `source-sha256.json` fingerprints all three versions and the shared final harness.

Build every version using the same current harness, with `QueueSources` pointing to its source directory. Place executables in `original/`, `earlier/` and `current/` under a fresh benchmark directory. Run:

```sh
python3 run-retained.py /path/to/benchmark-directory
python3 summarize-retained.py /path/to/benchmark-directory > /path/to/benchmark-directory/summary.txt
```

The runner defaults to full throughput and batch matrices. Optional trailing modes, such as `batch-sparse buffered-single`, run three sparse blocks and two buffered blocks. Output files with the same names are overwritten, so use a fresh directory for each run. Batch modes disable tiering; throughput modes use normal tiering.

## Completed target controls

`target-controls/control-*` retains the complete eight-slot confirmation sequence O/E/C/K/K/C/E/O. There are three isolated sparse blocks and two buffered single-thread blocks. In these filenames and their summary, `original` = O, `earlier` = E, `prior` = retained C, and `final` = rejected K. That last label reflects its status as a candidate when measured, not retained production code.

K was rejected after these targets. The runner was stopped during its later throughput matrix. The resulting incomplete `exploratory/stopped-k-*` records are preserved but excluded from all report tables. `exploratory/run-k-confirmation.py` records that experiment's full intended schedule.

## Rejected candidates

Apply each patch to a fresh copy of current source, never on top of another candidate patch. These patches reconstruct benchmark experiments, not proposed production changes.

| Patch | Experiment |
|---|---|
| a | Inline the pop dispatcher |
| b | A plus outline unbuffered push |
| c | B plus outline the cold push transaction call |
| d | Save only the deletion root in a specialized pop transaction |
| e | D plus inline the pop dispatcher |
| f | Move the pop transaction guard into buffered pop |
| g | F plus move the push guard into buffered push |
| h | G plus outline unbuffered push |
| i | Reuse routing's comparison in insertion, with a conditional-expression routing shape |
| j | Same reuse with the original short-circuit routing shape |
| k | J plus inline deletion-buffer insertion |
| l | Inline insertion without comparison reuse |
| m | Separate unbounded enqueue from bounded reservation cleanup |
| n | M plus explicit inlining of the dispatcher and shared loop |

Patches live under `exploratory/patches/`. `k-confirmation.patch` also includes the private parameter documentation added for K's full solution build; it matches that confirmation source fingerprint. None of these patches was retained.

All `check-*` and `explore-*` outputs are exploratory and are not pooled into the retained matrices. The original exploratory runner scripts preserve their temporary paths and build commands. Repoint those paths to fresh source/output directories when reproducing; the retained runner above is parameterized.

**Excluded overlap:** the end of I's full-batch trial overlapped J's build/early trial. Those I/J exploratory results are kept for transparency, but were not used for the report's confirmation tables or retention decision. K's completed target controls, the M/N trials and the final retained matrices ran serially without competing builds/tests/benchmarks.

## Diagnostics and verification

`diagnostics/` includes selected generated-code listings and transaction counts. The instrumented transaction-count build changes private diagnostics only; its timing lines are not performance evidence. The two diagnostic patches apply separately to a source copy and harness copy; build with `QueueSources` pointing to the instrumented source. The instrumented harness predates `trace-ties`, which its patch accounts for.

`verification/` contains the final retained-source build, all three test suites, Native AOT publish/run, and original/current traces and quality records. The AOT publish used the existing command-line SourceLink audit workaround described in the report. The production source fingerprints match the start-of-investigation snapshot byte-for-byte.
