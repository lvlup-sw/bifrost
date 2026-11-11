# Mutation and boundary evidence

Each row names an observed negative control against production code. A baseline defect is a negative control when the regression fails before the separate fix. Synthetic oracle-only checks do not count here. The linked evidence reports exact commands, source revisions, test names and results.

| Invariant | Production control detected | Boundary and evidence |
|---|---|---|
| Q1 | Clear omitted | Successful-effects multiset and separate Clear accounting; queue-generated-evidence.md |
| Q2 | Capacity comparison >= changed to > | Capacity equality, rejection snapshot and refill; queue-generated-evidence.md |
| Q3 | Published minimum winner reversed | Quiescent lawful forward/reverse comparers and ties; queue-generated-evidence.md |
| Q4 | Reservation rollback omitted after comparer throw | 884 attempts, 740 reached throws, drain/refill recovery; queue-generated-evidence.md |
| Q5 | Duplicate successful publication; verification bypass; contention treated as empty | Controlled actors, actual overlapping call intervals, resident-survivor proof; q5-observed-empty.md |
| P1 | Boost subtraction changed to addition | Four timestamp frequencies and exact representable units; priority-scaling-evidence.md |
| P2 | Interactive threshold mapped to Batch | Capacity/fraction table and adjacent occupancy; priority-scaling-evidence.md |
| O1 | Drain changed to Abort | Gated handlers, wait cancellation and closed admission; lifecycle-invariants.md |
| O2 | Failure outcome published as success | Independent production success/failure/cancellation outcomes and worker exits; independent-review.md |
| R1 | Retry budget increased by one | Budgets 0–3, failures before/after admission effects, cancellation/timeout; lifecycle-invariants.md |
| D1 | DropWrite instead of DropOldest; baseline false drop accounting | Capacity one, early drain, concurrent read/write and saved replay; lifecycle-invariants.md |
| S1 | Next-fire plus changed to minus; cadence boundary mutants 22 and 41 | Null/earlier/equal/future anchors, strict progress, translation; tool-pilots.md |
| S2 | Baseline cap-before-Coalesce selection | 99/100/101/500 periods, three policies, startup dispatch, 500→101 reduction; scheduler-invariants.md |
| S3 | Failed-register compensation omitted; baseline stale-generation writes | Before/after store effects, replacement races, committed state; registry-invariants.md |
| S4 | Checkpoint omitted; baseline checkpoint before handoff | Frozen restart images, handoff before/after effects, occurrence identity; registry-invariants.md |
| A1 | High watermark > changed to >=; cooldown < changed to <= | Adjacent floating-point watermarks and 0/99/100/101 ticks; priority-scaling-evidence.md |
| A2 | Baseline Stop callback race and disposed running state | Gated entered callback, failed-action recovery, awaited restart/disposal; independent-review.md |

## Survivor decisions

The Stryker trial classifies each surviving and uncovered cadence mutant in `tool-pilots.md`. Equivalent and unreachable cases are separate from unclosed RNG-dependent obligations. Suppressed jitter and conditional clamp preferences need controlled random draws for deterministic distinction. The proposal's generated interval domain uses zero jitter; no whole-library mutation adequacy is claimed. A future RNG seam needs component review. This limitation is not a passing score.

DropNewest at capacity one is equivalent to DropOldest and survived as expected. The selected queue/lifecycle scripts require actual test failures and restore the original source. The Q5 harness additionally rejects watchdog/build failures as kills. Coverage alone does not show fault sensitivity.

## Coverage requirements

Keep the existing 80% line and branch floor for each shipping assembly, with its existing generated-code exclusions. Run stress separately from instrumentation. Preserve both NativeAOT smoke gates. No threshold or exclusion was relaxed for these changes.

The boundary obligations above complement structural coverage: equality and adjacent values, all three policies, empty/singleton/full state, reached exceptions and recovery, before/after effects, closed versus aborted lifecycle, and stale versus current generation. They do not claim formal MC/DC measurement or exhaustive schedule coverage. The exact measured assembly values and test results are retained in `integration-evidence/`.
