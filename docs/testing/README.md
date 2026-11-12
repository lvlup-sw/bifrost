# Invariant testing

Run from the repository root with Python 3 and the .NET 10 SDK:

```sh
python3 scripts/run-invariant-tests.py
dotnet run --project src/Bifrost.Tests -c Release
dotnet run --project src/Bifrost.Tests.Concurrency -c Release
dotnet run --project src/Bifrost.Tests.Scheduling -c Release
```

The runner builds three test assemblies and selects 27 invariant suites. Every selection must execute at least one test. It rejects failed builds, failed tests, missing reports, and zero discovery. Use `--dotnet /path/to/dotnet` to select an SDK and `--output /path/to/results` to change the output directory. `--skip-build` requires current Release binaries unless `--configuration` selects another build.

[The catalog](invariant-catalog.json) maps 17 property families to their domains, tests, and limits. The tests cover queue accounting and ordering, admission, lifecycle outcomes, retries, DLQ conservation, scheduling, persistence, and autoscaling. Additional suites check cron equivalence, per-job gate lifetime, and checkpoint monotonicity.

## CI and generated artifacts

Pull requests run the invariant suites alongside existing build, coverage, stress, and NativeAOT checks. Nightly and manual runs set `BIFROST_INVARIANT_SEED_MULTIPLIER=4` and run these targeted production mutations:

```sh
python3 scripts/invariants/queue-mutation-check.py
python3 scripts/invariants/lifecycle-mutations.py
```

Run mutations serially in a disposable checkout. They temporarily change production source, require actual test failures, and restore source in `finally` blocks. Do not run builds, tests, or other mutations concurrently in that checkout. Rebuild after mutation checks before running another suite.

Generated logs, TRX reports, source revision, commands, counters, replay histories, and mutation results belong in ignored `artifacts/invariants/`. CI uploads that directory even after failures and retains it for 30 days. Do not commit run logs or tool-installation output. The queue multiplier expands generated seeds; it does not expand every component domain or reproduce native thread schedules.

## Replay and reduction

The shared helpers in `src/Bifrost.Testing.Systematic` validate command histories, compare real component execution against independent models, and reduce failures while retaining their failure class. Reduction establishes minimality only within its supported transformations.

Required fixtures live with the tests and are copied to the test output:

- `src/Bifrost.Tests/Systematic/synthetic-fifo-counterexample.json`
- `src/Bifrost.Tests/Systematic/lifecycle-counterexample.json`
- `src/Bifrost.Tests.Concurrency/queue-clear-replay.json`
- `src/Bifrost.Tests.Scheduling/scheduler-coalesce-replay.json`

The ordinary tests execute these saved histories against the current implementation. The synthetic fixture intentionally demonstrates a faulty model. Production fixtures assert corrected behavior by default. `BIFROST_REPLAY_EXPECT_FAILURE=1` is only for deliberately restored faulty implementations and requires exact saved failure observations.

To use a different production fixture, set `BIFROST_REPLAY_FILE` and select its test:

```sh
BIFROST_REPLAY_FILE="$PWD/src/Bifrost.Tests/Systematic/lifecycle-counterexample.json" dotnet run --project src/Bifrost.Tests -- --treenode-filter '/*/*/LifecycleInvariantTests/D1_SavedCounterexampleExecutesPersistedHistory'
```

Queue and scheduler replay classes are `QueueReplayTests` and `SchedulerReplayTests`. Registry replay uses `BIFROST_REGISTRY_REPLAY` with `SystematicRegistryTests/ReplaySavedHistory`; restart replay uses `BIFROST_REGISTRY_RESTART_REPLAY` with `SystematicRegistryRestartReplayTests/ReplaySavedRestartHistory`. Restart tests generate and read their own fixtures when no file is supplied. Set `BIFROST_REGISTRY_ARTIFACTS` to retain generated registry histories; the runner sets it automatically.

## Scope

These tests cover finite histories and declared domains. They do not prove absence of all defects, reconstruct arbitrary OS schedules, or simulate a real database crash. Priority arithmetic covers integer truncation and signed 64-bit saturation within valid windows. Jitter/custom-cadence performance, ambiguous external store effects, and unsupported reentrant callbacks need separate assessment. See [issue #85](https://github.com/lvlup-sw/bifrost/issues/85).

[Scheduling design and benchmark summary](scheduling-overhead.md) explains the selected optimizations. [Exarchos testgen pilot](testgen-pilot.md) records the local generator qualification and adoption scope. The benchmark harness remains in `tools/scheduling-overhead`; exploratory FsCheck, Stryker, Coyote, Daikon, and SMT pilots are not maintained repository tooling.
