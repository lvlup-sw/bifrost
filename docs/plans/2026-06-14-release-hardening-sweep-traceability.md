# Spec Traceability — Release-Hardening Sweep

**Design:** `docs/designs/2026-06-14-release-hardening-sweep.md`
**Plan:** `docs/plans/2026-06-14-release-hardening-sweep.md`

## Traceability Matrix (requirement → tasks)

| Design Requirement | Key Requirements (acceptance) | Task ID(s) | Status |
|--------------------|-------------------------------|------------|--------|
| **DR-1** — Pin Actions to SHAs (#19) | No `@vN` refs remain; SHAs `# vN`-annotated; CI green; Dependabot added | 1 | Covered |
| **DR-2** — Bump OpenTelemetry.Api (#20) | `OpenTelemetry.Api` ≥1.15.3 in CPM only; no NU1902; OTel tests green | 2 | Covered |
| **DR-3** — Dispose queue resources + trim per-wait alloc (#21) | Bindings dispose `_signal` (idempotent, no CA2213); orchestrator disposes owned `_queue`; per-wait alloc 0 B via benchmark; ODE/double-dispose edge cases | 3, 4, 5, 6 | Covered |
| **DR-4** — Raise Bifrost.Concurrency to ≥80% (#24) | ≥80% line+branch in isolation; each #18 defensive branch directly tested (ctor validation, reservation rollback, ToArray clamp, SubQueue.Grow clamp, TryDequeueMin PopHeldRoot); strict-min correctness; TUnit awaited | 7, 8, 9, 10, 11, 12, 13, 14 | Covered |
| **DR-5** — Per-assembly coverage gate (#24) | Gate fails if any shipping assembly <80% line **or** branch (re-based from per-project file gating — DR-6); per-assembly PR table; lands with/after DR-4 so `main` stays green; assembly-set defined | 15, 16, 17, 18 | Covered |

All five requirements are covered. Non-requirement design sections (Problem Statement,
Chosen Approach, Options Considered, Technical Design, Integration Points, Testing
Strategy, Risks, Open Questions) inform scope and are realized through the DR tasks above
— no orphan requirements, no task without a DR anchor.

## Error-handling / edge-case coverage (mandatory)

- **DR-3:** double-`Dispose` idempotency, post-dispose `ObjectDisposedException` on wait,
  orchestrator double-`DisposeAsync` safety (Tasks 3–5).
- **DR-4:** reservation-rollback `catch` via throwing comparer, bounded+full
  `InvalidOperationException`, ctor reject `0`/`<-1`, seqlock tear/retry, overflow/grow
  clamps (Tasks 9, 10) — with a documented seam-or-`[ExcludeFromCodeCoverage]` disposition
  for genuinely unreachable clamps (Task 14).
- **DR-5:** branch-below-threshold and any-project-below-threshold both fail the gate
  (Tasks 15, 16).

## Scope Declaration

**Target:** in-repo CPQ-port debt gating the 0.5.0 tag — supply-chain pin (#19), security
advisory bump (#20), priority-queue resource disposal + alloc trim (#21), and the
`Bifrost.Concurrency` coverage gap + per-project gate (#24).

**Excluded (with cause):** #22 (DR-8 soak — long real-hardware run, release ceremony),
#23 (DataFerry freeze — out-of-repo), #5 (zero-alloc event stream — deferred per the
issue), #32 (scheduling follow-ups — orthogonal subsystem). ESA-2021-parity milestone
issues (#26–#30) are out of scope by the original request.
