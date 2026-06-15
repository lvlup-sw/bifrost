# Design: 0.5.0 Release-Hardening Sweep

**Bundle:** #19, #20, #21, #24 — deferred CPQ-port debt + stabilization items
**Feature ID:** `release-hardening-sweep`
**Date:** 2026-06-14

## Problem Statement

The roadmap (`docs/designs/2026-02-02-roadmap-to-0.5.0.md`) defines **0.5.0 as
"Stabilization + Priority Queues."** Priority queues have now landed (#18 CPQ port,
#31 transition bitmask), so the only thing standing between `main` and a confident
0.5.0 tag is a cluster of debt that was *deliberately deferred* from the #18 review to
keep that PR scoped. Four of these are in-repo, code/CI/test-shaped, need no external
hardware, and share one theme — **make the repo tag-ready**:

- **#19** — three of four workflows still pin GitHub Actions to mutable `@v4` tags
  (supply-chain risk; zizmor `unpinned-uses`). Only `soak.yml` was pinned in #18.
- **#20** — `OpenTelemetry.Api` 1.14.0 carries advisory **CVE-2026-40894 /
  GHSA-g94r-2vxg-569j** (Moderate; baggage/B3/Jaeger header DoS), emitting **NU1902**
  on every restore of `Bifrost.OpenTelemetry` and `Bifrost.Tests`. Patched in **1.15.3**.
- **#21** — the priority `IWorkQueue` bindings leak an undisposed `SemaphoreSlim`
  (CA2213-class), `WorkOrchestrator` never disposes a queue that owns disposable
  resources, and the priority `WaitToDequeueAsync` allocates on every wait.
- **#24** — `Bifrost.Concurrency` sits at **62.1% line / 51.7% branch**, well under the
  repo's 80% standard (CLAUDE.md; roadmap DR-1). The shortfall hides because the
  coverage gate checks a *merged aggregate* that the orchestrator suite pulls up.

**Success:** all four resolved, `main` stays green, and the 80% gate becomes
*honest per-package* so this class of gap can't hide again before 0.5.0.

## Chosen Approach

**One cohesive hardening PR, sequenced so the gate flips last.** All four issues are
CPQ-port wrap-up; a reviewer can reason about them as a unit. They split into two
independent quick wins (#19, #20) and two coupled queue/coverage items (#21, #24),
where #24 itself has a hard ordering constraint: the per-project gate (DR-5) must land
**with or after** the new Concurrency tests (DR-4), or it reds `main` immediately.

Sequencing within the bundle:

1. **#19, #20** — independent, mechanical, parallelizable; verify CI stays green.
2. **#21** — queue-resource disposal + alloc trim; verified by the existing
   orchestrator allocation benchmark.
3. **#24 tests (DR-4)** — raise `Bifrost.Concurrency` to ≥80% line/branch in isolation.
4. **#24 gate (DR-5)** — flip the coverage gate to per-project enforcement **only
   after** DR-4 proves the package clears 80%.

Note: #24's original "gap 1" (Concurrency tests not running in CI) is **already fixed** —
`ci.yml:46` globs `src/Bifrost.Tests*/*.csproj`, so the sibling suite already runs and
is measured. DR-4/DR-5 are the remaining work: write the tests, then make the gate
per-project.

**Explicitly out of scope** (excluded with cause, not forgotten): **#22** (DR-8 600 s
soak + README finalization) needs a long run on real hardware near tag-time and overlaps
#30's Xeon VM — a standalone release step, not a code PR; **#23** (DataFerry freeze) is
an out-of-repo chore in `lvlup-sw/DataFerry`; **#5** (zero-alloc event stream) is
explicitly "defer unless event-heavy workloads anticipated"; **#32** (scheduling
follow-ups) is a different subsystem and would dilute this CPQ-themed review.

## Options Considered

Three bundles were weighed before settling on the Chosen Approach above.

### Option 1: Release-hardening sweep (#19, #20, #21, #24) — CHOSEN

**Approach:** Bundle the four in-repo deferred items — pin Actions to SHAs, bump
OpenTelemetry off the advisory, dispose priority-queue resources + trim the per-wait
allocation, and raise `Bifrost.Concurrency` to 80% with a per-project gate — into one
CPQ-port-wrap-up PR.

**Pros:**
- Tightest bundle that actually unblocks a confident 0.5.0 tag.
- All in-repo, no external hardware; single theme a reviewer reasons about as a unit.
- Makes the 80% gate honest per-package so this class of gap can't hide again.

**Cons:**
- Effort is dominated by #24's test-writing across six surfaces.
- Mixes trivial (one-line bump) with substantial (coverage) work in one review.

**Best when:** the goal is to clear exactly the debt gating the 0.5.0 tag with one
coherent, in-repo review unit.

### Option 2: Quick hygiene wins (#19, #20, #21)

**Approach:** Ship only the three small mechanical items — pin Actions, bump OTel,
dispose/alloc-trim — as one fast PR; defer the larger #24 coverage work to its own effort.

**Pros:**
- Fast, low-risk, mergeable in a single session.
- Clears the security advisory and supply-chain risk immediately.

**Cons:**
- Leaves the #24 coverage gap (a stated DR-1 standard) open, so 0.5.0 still can't be
  tagged honestly.
- The coverage work returns as a separate stream anyway.

**Best when:** you want the lowest-risk subset out today and are content to track #24
separately.

### Option 3: Deferred-debt cleanup (#19, #20, #21, #24, #32)

**Approach:** Option 1 plus scheduling post-MVP follow-ups (#32: 5 test-hygiene + 5
LOW/MEDIUM review items) for the broadest single-pass debt clear.

**Pros:**
- Clears the most deferred debt in one delegation run.
- Closes the scheduling review loop from #25 at the same time.

**Cons:**
- Spans two subsystems (CPQ + scheduling), enlarging the delegation/review surface.
- Dilutes the single CPQ-wrap-up theme; harder to reason about as one review.

**Best when:** capacity is high and a wide debt-clearing sweep outweighs review focus.

**Rationale for Option 1:** it clears exactly the in-repo debt that gates the 0.5.0 tag,
with one coherent theme, while leaving genuinely orthogonal work (#32 scheduling) and
non-PR-shaped work (#22 soak, #23 out-of-repo, #5 deferred perf) out of scope.

## Technical Design

Five touch-points, each isolated to its issue, no shared mutable surface between them:

- **CI workflows** (DR-1) — `.github/workflows/{ci,publish,project-automation}.yml`:
  rewrite mutable `@vN` `uses:` refs to 40-char SHAs `# vN`; optional `.github/dependabot.yml`.
- **Central Package Management** (DR-2) — `src/Directory.Packages.props`: single
  `OpenTelemetry.Api` version bump 1.14.0 → ≥1.15.3.
- **Priority queue bindings** (DR-3) — `ConcurrentPriorityWorkQueue` /
  `LockingPriorityWorkQueue` gain `IDisposable`/`IAsyncDisposable` disposing their
  `SemaphoreSlim` (+ owned `_queue`); `WorkOrchestrator.DisposeAsync` forwards disposal
  when the binding owns resources; the priority `WaitToDequeueAsync` drops its per-wait
  allocation (cache the wait state / pooled completion source).
- **Concurrency tests** (DR-4) — new tests in `src/Bifrost.Tests.Concurrency` targeting
  `Inspect.cs`, `Collection.cs`, bounded-capacity edges, `SubQueue.cs`,
  `PriorityComparerHelpers.cs`, and stickiness.
- **Coverage gate** (DR-5) — `scripts/ci/coverage-gate.sh` + `.github/workflows/ci.yml`:
  evaluate each `TestResults/<project>.cobertura.xml` independently; fail on any
  sub-threshold project; surface per-project results in the PR comment.

## Integration Points

- **`Bifrost.OpenTelemetry` / `Bifrost.Tests`** (DR-2) — consume `OpenTelemetry.Api`;
  must compile and pass OTel integration tests against 1.15.x with NU1902 cleared.
- **`WorkOrchestrator` ↔ priority `IWorkQueue` bindings** (DR-3) — disposal ownership
  contract: the orchestrator forwards `DisposeAsync` to a resource-owning queue binding.
- **`Lvlup.Build` analyzers / warnings-as-errors** (DR-3) — CA2213 must be clean.
- **CI `Build & Test` + `Coverage Gate`** (DR-1, DR-5) — the pinned actions and the
  per-project gate run inside the existing CI pipeline; both must keep `main` green.
- **`coverage-gate.sh` callers** (DR-5) — the script's contract changes from
  aggregate-once to per-project; any other caller of the script inherits the new behavior.

## Testing Strategy

- **DR-1/DR-2** — verified by a green CI run post-change plus a `grep` assertion that no
  `@vN` refs remain and a restore/build emitting no NU1902.
- **DR-3** — a disposal unit test (semaphore/queue disposed exactly once, double-dispose
  safe, no `ObjectDisposedException` leaks to a normal shutdown caller) + the orchestrator
  allocation benchmark for the per-wait-allocation claim.
- **DR-4** — direct tests per defensive/edge branch (ctor validation, reservation
  rollback via throwing comparer, `ToArray` overflow clamp, `SubQueue.Grow` clamp/throw,
  `TryDequeueMin` `PopHeldRoot`) + strict-min correctness (global min, contention rescan,
  no element lost, empty semantics); coverage measured in isolation by `coverage-gate.sh`.
- **DR-5** — a fixture cobertura set proving the gate fails when one project is <80% and
  passes when all clear 80%. All TUnit assertions are awaited.

## Requirements

### DR-1 — Pin all GitHub Actions to commit SHAs (#19)

Pin every `uses:` in `ci.yml`, `publish.yml`, and `project-automation.yml` to a full
40-char commit SHA annotated with `# vN` (matching the pattern already in `soak.yml`).
Optionally add `.github/dependabot.yml` (`package-ecosystem: github-actions`) so pins
stay current. Actions in scope: `actions/checkout@v4`, `actions/setup-dotnet@v4`,
`actions/upload-artifact@v4`.

**Acceptance criteria:**
- `grep -rE 'uses:.*@v[0-9]+' .github/workflows/` returns **no matches** — every
  reference is a 40-char SHA with a `# vN` comment.
- Each pinned SHA resolves to the tip of the action's current major release tag.
- CI (`Build & Test`, coverage, AOT smoke) remains green after pinning — no workflow
  breaks from a mis-pinned ref.
- (optional) Dependabot config present and valid (`actionlint`/GitHub parses it).

### DR-2 — Bump OpenTelemetry.Api off the advisory (#20)

Bump `OpenTelemetry.Api` from 1.14.0 to **≥ 1.15.3** (first patched version for
CVE-2026-40894) in `src/Directory.Packages.props` **only** (Central Package Management).
Verify no API breakage across the 1.14→1.15 line.

**Acceptance criteria:**
- `OpenTelemetry.Api` pinned to ≥ 1.15.3 in `Directory.Packages.props`; no version edits
  leak into any `.csproj`.
- `dotnet restore` / `dotnet build src/Bifrost.sln -c Release` emits **no NU1902** for
  `Bifrost.OpenTelemetry` or `Bifrost.Tests`.
- OpenTelemetry integration tests pass; the package compiles with no API-break fallout
  (failure mode: a removed/changed 1.15 symbol surfaces as a build error, not a silent
  runtime regression).

### DR-3 — Dispose priority queue resources + trim per-wait allocation (#21)

Dispose the `SemaphoreSlim` (and any owned queue) in `ConcurrentPriorityWorkQueue` /
`LockingPriorityWorkQueue`; have `WorkOrchestrator.DisposeAsync` dispose `_queue` when
the binding owns `IDisposable`/`IAsyncDisposable` resources; remove or amortize the
per-wait allocation in the priority `WaitToDequeueAsync` path.

**Acceptance criteria:**
- Disposable queue resources are released on `WorkOrchestrator.DisposeAsync` (verified by
  a disposal test asserting the semaphore/queue is disposed exactly once).
- **No CA2213 / undisposed-`IDisposable`-field analyzer findings** on the bindings under
  warnings-as-errors.
- Per-wait allocation removed or amortized — confirmed by the orchestrator allocation
  benchmark (steady-state wait allocates 0 B, or a documented reduction).
- **Failure modes:** disposal is idempotent (double-dispose is safe); disposing a queue
  with a wait in flight does not deadlock and does not surface an `ObjectDisposedException`
  to a normal shutdown caller.

### DR-4 — Raise Bifrost.Concurrency to ≥80% line/branch (#24, test work)

Drive `Bifrost.Concurrency` to **≥80% line and ≥80% branch** via
`Bifrost.Tests.Concurrency`, measured in isolation by `scripts/ci/coverage-gate.sh`.
Cover the under-tested strict-min path (`Inspect.cs`), collection surface
(`Collection.cs`), bounded-capacity edges, `SubQueue`, `PriorityComparerHelpers`, and
stickiness. New tests follow the TUnit "assertions must be awaited" convention.

**Acceptance criteria:**
- `coverage-gate.sh --coverage-file <concurrency.cobertura.xml> --threshold 80` passes:
  ≥80% line **and** ≥80% branch in isolation.
- **Each #18 defensive/edge branch has ≥1 direct test:** internal-ctor `boundedCapacity`
  validation (reject `0` / `< -1`); the reservation **rollback `catch`** (driven by a
  throwing comparer); `ToArray` `long`-accumulate overflow clamp; `SubQueue.Grow`
  `Array.MaxLength` clamp + "cannot grow further" throw; `TryDequeueMin` `PopHeldRoot`.
- Strict-min path returns the true global min single-threaded, rescans/revalidates under
  contention, loses no element vs. the relaxed path, and reports correct empty semantics.
- All new tests `await` their assertions; full Concurrency suite green in Release.

### DR-5 — Per-project coverage-gate enforcement (#24, CI work)

Make the coverage gate fail when **any single** test project is <80% line or <80% branch,
rather than only checking the merged aggregate. Surface per-project results in the PR
comment. Land **with or after** DR-4.

**Acceptance criteria:**
- The gate evaluates each `TestResults/<project>.cobertura.xml` independently and fails
  the build if any project is <80% line or <80% branch.
- Per-project pass/fail is visible in the CI PR comment.
- **Sequencing guard:** the per-project gate is not enabled while `Bifrost.Concurrency`
  is below 80% — DR-4 lands first (or in the same commit), so `main` never goes red.
- **Edge case:** the project set the gate applies to is defined explicitly (e.g. exclude
  pure test-support projects with no shippable code) so the gate fails on real shortfalls,
  not on artifacts of the report set.

## Risks & Mitigations

- **#24 dominates effort** (test-writing across six surfaces). Mitigation: it is
  decomposable by file (`Inspect`, `Collection`, bounded edges, `SubQueue`, comparer,
  stickiness) and parallelizable across implementers; DR-5 is gated behind it so a
  partial DR-4 can't red `main`.
- **OTel 1.15 API drift** (DR-2): low — only `OpenTelemetry.Api` is referenced; a break
  surfaces at compile time and is caught before merge.
- **Mis-pinned action SHA** (DR-1): caught by the post-pin CI run going red.

## Open Questions

1. **#19 Dependabot** — adopt `.github/dependabot.yml` for `github-actions` now (keeps
   pins fresh automatically), or pin-only this round? *(Default: add it — recommended in
   the issue.)*
2. **DR-5 project scope** — should the per-project gate cover *all* test projects, or only
   the shipping packages' suites (excluding test-support projects)? *(Default: shipping
   packages' suites.)*
3. **PR shape** — single hardening PR (one review unit, matches the bundle theme) vs.
   split the two quick wins (#19/#20) from the queue/coverage work (#21/#24)? *(Default:
   single PR; revisit at plan-review if #24 balloons.)*
