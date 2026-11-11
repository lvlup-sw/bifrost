# Systematic testing implementation

The approved proposal covers 17 invariant families. `invariant-catalog.json` retains the complete catalog and its evidence status. A proposed entry is not a completed test.

## Coordination

Use native agents and isolated Git worktrees. One integration owner reviews results and merges changes serially. Dispatch ready tasks when their dependencies finish. Keep reproduction tests and production fixes in separate commits. Do not use Exarchos.

## Dependencies

| Work | Depends on | Acceptance evidence |
|---|---|---|
| S1/S2 scheduler | Fresh remote-main baseline | Interval oracle, all policies, startup dispatch, valid reduction, baseline failure and negative control |
| History tools | Baseline | Replay schema, actual replay, valid reduction, failure-class preservation |
| Q1–Q5 queue | Baseline; history integration later | Independent multiset, metamorphic relations, reached faults, controlled schedules and native stress |
| O1/O2/R1/D1 lifecycle | History tools | Identity-aware ledger, independent models, cancellation and before/after-effect faults |
| S3/S4 registry/restart | History tools and scheduler | Commit-boundary model, stale-generation races, crash boundaries, occurrence identity |
| P1/P2/A1/A2 priority/scaling | History tools | Exact unit conversions, decision tables, boundary equality, gated callback histories |
| Tool trials | Scheduler and history tools | FsCheck, Stryker discovery/failure/mutant checks, Coyote control limits, held-out Daikon falsification, bounded SMT |
| CI and adequacy | Component evidence and trials | Discovery guard, retained replay artifacts, mutation matrix, measured lane limits; preserve existing gates |
| Completion audit | Integrated results | Every catalog item and proposal requirement mapped to inspected evidence and explicit limits |

Finite tests cannot show that all latent defects are absent. A seed does not reproduce an OS thread schedule. Record attempted and reached fault positions separately. A passing model-only negative control does not establish sensitivity to a production mutation.
