# Invariant CI lanes

`python3 scripts/run-invariant-tests.py` builds the three test assemblies and executes each named invariant suite with Microsoft.Testing.Platform. Each selection requires at least one test. The runner also reads the new TRX file and rejects a suite with no executed tests. It deletes an earlier TRX file before each invocation so stale output cannot satisfy discovery. Test failures and build failures produce a nonzero exit.

The runner retains command lines, source SHA, dirty status, configuration, seed multiplier, exit codes, TRX counters and durations in `artifacts/invariants/`. Generated queue replay failures and registry source/reduced histories write under that directory. Log files retain inline failure histories for families without automatic file capture. Only synthetic payloads are generated.

| Lane | Executed checks | Limit and evidence |
|---|---|---|
| Pull request | Default deterministic invariant suites; existing build/coverage/stress/AOT workflow remains in place | New invariant job has a 20-minute ceiling. Per-suite logs/TRX and source provenance retained 30 days |
| Nightly/manual | Same suites with four times the queue seed corpus, then selected queue and lifecycle production mutations | Mutations run serially, restore source in finally blocks, and require actual assertion failures. Artifact upload runs after failures |
| Periodic/release | Existing CPQ soak, native stress and AOT workflows; bounded solver checks run with the explicit commands in solver/README.md | Existing soak has a 120-minute ceiling. Solver queries have explicit 10/20/30-second limits. No blanket proof is claimed |

Final wider-lane validation passed 19 selected suites and 106 executed cases in 18.756 seconds including incremental builds. No suite had zero execution. The scheduler default reduction entry now checks that a successful production history cannot be reduced as a failure; explicit baseline mode remains separately tested. `integration-evidence/final-invariants.json` records source, commands, TRX counts and durations. The earlier `ci-evidence/nightly-summary.json` is retained as historical runner validation. The queue multiplier expands only generated seed loops, not every component's configuration domain or native thread scheduling.

A deliberately nonexistent test selection with `--minimum-expected-tests 1` exited 8. `ci-evidence/zero-discovery.log` retains that negative control. Workflow lint passed after registering the existing organization's `blacksmith-4vcpu-ubuntu-2204` runner label in the linter's local configuration. No other lint diagnostics were suppressed.

These runtime observations justify the initial job ceiling; they do not measure remote runner provisioning or NuGet restore latency. The workflow has not run on GitHub until its branch is published. Existing coverage, stress and AOT gates have not been removed or weakened. Tool-specific pilot results determine whether preview integrations belong in a required lane.
