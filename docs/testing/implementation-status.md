# Systematic testing implementation

The approved revision 3 proposal is implemented across all 17 invariant families and nine metamorphic relations. The catalog records contracts, domains, oracles, observation boundaries, owners, evidence status and limitations. `mutation-and-boundary-matrix.md` maps every family to a production negative control.

## Execution and review

Native agents worked in isolated Git worktrees. The integration owner dispatched tasks as dependencies cleared and integrated commits serially. Tests and candidate production fixes remain separate review units. Exarchos was not used. Remote main was fetched at the start and before final validation; both resolved to `0fc121384950d964722a8de36d72a26679a428d3`.

| Package | Implemented result | Evidence |
|---|---|---|
| WP1 | S1/S2 independent interval oracle, all missed-fire policies, startup occurrence, cap boundaries, baseline reduction to 101, separate Coalesce fix | scheduler-invariants.md; replay-integration.md |
| WP2 | Shared semantic command/replay format, validity checks, deterministic same-failure reducer, actual baseline/fixed file replay | history-tools.md; replay-integration.md; independent-review.md; registry-review.md |
| WP3 | Queue, priority/admission, lifecycle/outcomes, retry, DLQ, registry/restart and autoscaling models | invariant-catalog.json; component evidence reports |
| WP4 | Reached fault accounting, before/after-effect failures, actual overlap, controlled actor orders, native stress and synthetic crash images | queue-generated-evidence.md; q5-observed-empty.md; lifecycle-invariants.md; registry-review.md |
| WP5 | Per-invariant negative controls, classified Stryker survivors, measured tool pilots, discovery/failure guards, PR/nightly CI and retained artifacts | mutation-and-boundary-matrix.md; tool-pilots.md; ci-invariants.md |

Independent review found and closed further gaps: disposed autoscaling state, saved DLQ replay, actual worker-outcome observations, overlapping queue false-return checks, saved restart replay, idle-barrier ordering and checkpoint generation/metadata races. Historical report sections retain their original revisions; integration follow-ups identify later corrections.

## Final validation

With task-local SDK 10.0.404 and runtime 10.0.15:

- Main tests: 825 passed.
- Concurrency tests: 160 passed, including the native stress category. The separate stress run passed 8 tests.
- Scheduling tests: 359 passed after the overhead follow-up.
- Wider invariant lane: 22 suites, 117 cases, four times the queue seed corpus; all passed.
- Each shipping assembly exceeded the existing 80% line and branch floor with the existing generated-code exclusions.
- Both NativeAOT samples passed. Scheduling was republished after the final scheduler fixes.
- Zero discovery failed as intended. The exact nightly queue and lifecycle mutation commands detected all selected faults and restored source.
- Workflow syntax lint passed with the existing custom runner label configured.

Commands, counts and logs are in `integration-evidence/`. The original installed 10.0.303 SDK failed the AOT security audit for ILCompiler 10.0.11. A task-local patched SDK resolved that environment issue; warnings and security checks were not disabled.

## Limits

Finite histories do not prove the absence of every latent defect. Native schedules are not reconstructed from seeds. P1 uses exact representable units. Scheduler arithmetic generation uses zero jitter and representable dates. Generic Coalesce retains one occurrence but scans an advancing backlog; its zero-jitter interval path is constant-time and built-in cron uses reverse calendar lookup. Custom cadence CPU bounds remain unresolved.

Stryker's RNG-dependent survivors are classified, not counted as adequate. Coyote explores selected task boundaries and does not model every native lock/channel operation. Daikon results remain hypotheses. Solver proofs cover declared arithmetic bounds. Restart tests use frozen durable images rather than killing a process. Arbitrary synchronous reentrant custom stores/event sinks and concurrent autoscaling restart during Stop have no new ordering guarantee.

These limits retain the proposal's scope. Production deployment and merging the candidate branch are not part of this implementation handoff.

## Overhead follow-up

`scheduling-overhead.md` records adoption of exact-name gates, cron reverse lookup, checkpoint monotonicity after ambiguous writes, benchmark results and limits. New regressions run in the invariant lane. Batching is tracked in issue #86. Earlier component reports describe historical stripe-based revisions.
