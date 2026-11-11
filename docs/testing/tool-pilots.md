# Measured tool compatibility pilots

Baseline: `24fbfd5`. Executed 2026-11-10 using .NET SDK 10.0.303 / runtime 10.0.11 on Linux. These are small compatibility trials, not exhaustive verification. No production or existing test changes are retained. Raw outputs and inputs are in `tools/invariant-pilots/results/`.

| Tool | Pinned version | Measured result | Decision |
|---|---|---|---|
| FsCheck | 3.4.0 | 1,000 valid zero-jitter `IntervalCadence.ComputeNextFire` checks passed; intentionally capped oracle failed and reduced in three successful shrinks to interval=1, last=0, steps=101 | Retain explicit domain generation/reduction; FsCheck can host it but does not replace replay schema or validity logic |
| Stryker.NET MTP preview | 4.13.0, TUnit 1.2.11 | 300 tests discovered; 31 cadence mutants tested, 21 killed, 10 survived; deliberate existing assertion failure detected | Compatible for this scoped suite; inspect survivors rather than setting a whole-project score gate |
| Coyote | 1.7.11 | Real admission-versus-drain lifecycle: 100 paths, 8 unique, zero bugs, three controlled operations per run; negative schedule control found one bug | Useful bounded task-boundary exploration, not a proof of native queue internals |
| Daikon | 5.9.0 | Actual C# outcome traces inferred candidates; held-out capacities falsified six confidence/filter-selected candidates (22 across unfiltered internal candidates) | Use only to propose hypotheses; no inferred relation is promoted to a contract |

## Reproduction

From repository root, set `DAIKON_JAR` to the installed 5.9.0 jar and run `bash tools/invariant-pilots/run.sh`. Run `bash tools/invariant-pilots/mtp-negative.sh` separately for the reversible assertion negative control. Dependencies install only into the pilot's `.tools` directory. NuGet restore needs network access. Runtime version and raw logs are retained. No external payload data is collected.

The exact executed jar was `/home/reedsalus/Documents/Codex/2026-11-10/ple/work/daikon-5.9.0/daikon.jar`. Java version is in `results/java-version.log`.

## FsCheck validity and reduction

The generator chooses interval ticks 1..1,000,000, last ticks 0..1,000,000, and elapsed whole steps 0..10,000. This keeps every computed date in range and fixes jitter to zero. Its oracle is `now + interval`, because generated `now` is exactly an aligned occurrence. This covers aligned boundaries only; it does not claim fractional offsets, negative dates, overflow, or jitter coverage.

The negative control evaluates the same actual production API against an intentionally obsolete capped oracle `last + interval * (min(steps,100)+1)`. It is not a production bug. Custom reductions preserve validity while lowering interval, last, and step count; raw FsCheck seeds and original/shrunk values are retained in `fscheck.log`. The measured minimal counterexample matches the intended failure boundary. Keep the explicit replay generator/reducer: FsCheck supplies trial orchestration and shrink reporting, but the domain-specific shrinker and saved-history format remain necessary. The pilot does not add FsCheck to the main tests.

## Stryker discovery and failure controls

Command: `dotnet-stryker --test-runner mtp --project Bifrost.Scheduling.Core.csproj --mutate '**/IntervalCadence.cs' --concurrency 1 --reporter json --reporter progress`, from `src/Bifrost.Tests.Scheduling`.

`stryker-mtp-full.log` records nonzero discovery and completed mutation runs. `mutation-report.json` preserves individual outcomes, source locations, and killing test references. Known production mutant 19 changes the initial next-fire expression from `now + this.interval` to `now - this.interval`; it was killed. Mutants 20 and 21 alter the subsequent interval addition and candidate comparison and were also killed. Reported scoped score is 65.62% (21 killed, 10 survived, one uncovered); it is not a whole-repository score. One compile-error mutant and filtered mutants do not count as kills.

The separate negative control changes only `SavedProductionCounterexample`'s expected command count from 101 to -999, invokes the same MTP runner, and restores the file with an EXIT trap. `stryker-assertion-negative.log` names that failing test and stops at the initial run. The runner's message says “more than 50% failing tests”; that text is retained, but should not be interpreted as 150 actual failures. The intentional assertion did not silently pass.

The first trial used `IntervalCadence.cs{90..105}` without the recursive glob. It discovered 300 tests but selected zero tested mutants; `stryker-mtp.log` is retained as a failed selection experiment. It supplies no mutation score or coverage evidence.

## Coyote boundary and runtime gap

The test constructs the actual `WorkOrchestrator<int>` (FIFO, capacity 2, zero native workers), races task-based admission with `DrainAsync`, awaits both, and asserts admission after close is rejected. `Pilots.dll` and its copied `Bifrost.dll` are rewritten; production source and main test binaries remain unchanged. `Bifrost.Core` contains the value/interface definitions used here; concurrency-library internals are outside this FIFO pilot.

Only .NET 10 runtimes were installed. The CLI initially failed requesting .NET 8; `DOTNET_ROLL_FORWARD=Major` enabled rewriting under .NET 10. The initial C# call `Task.WhenAll(producer,closer)` selected a newer overload and Coyote reported `UNCONTROLLED: System.Threading.Tasks.Task.WhenAll`. Explicitly using a `Task[]` eliminated that report. Both before/after logs are retained. This is a measured overload compatibility limitation, not evidence that every .NET 10 API is controlled.

The final run controls the two Task.Run operations, awaits, and task join at task boundaries. The seeded negative control races a task setting a local value with an assertion that it remains zero: Coyote found the bad schedule on the first explored path, with two controlled operations. Thus the engine actually explored scheduled task execution.

The real lifecycle still uses .NET `Lock`, volatile lifecycle state, and `System.Threading.Channels` internals. Their internal operations are not modeled by this pilot; absence of a reported uncontrolled call does not prove complete instrumentation. No active worker, blocking channel write/read, timer, cancellation race, or randomized relaxed-priority routing is exercised. Atomics and randomized stripe selection in `Bifrost.Concurrency` are outside the model. Preserve the existing native lifecycle, admission, priority, and concurrency tests as coverage for those paths. This pilot must not replace them.

## Daikon trace semantics and held-out challenge

`Lifecycle.Trace` explicitly emits C# dtrace records from actual `WorkOrchestrator` calls. Separate program points are Accepted, Rejected, Canceled, and Exception. Rejection is a full FIFO queue; cancellation is a pre-canceled `EnqueueAsync`; exception is an invalid work-class enum passed to `Enqueue`. All use zero workers, so before/after queue depth is stable. Traces include only capacity, before depth, after depth, and delta; no work payloads or IDs. This deliberately measures sequential admission, not concurrent conservation or handler outcomes.

Training capacities are 1 and 2; held-out capacities are 3, 7, and 16, with 12 repetitions each. Both files are produced by the real application before inference; Daikon learns only from training, and `InvariantChecker` evaluates that trained invariant set on held-out data. Repetitions are not independent configurations and must not be treated as statistical confidence.

Training candidates include accepted depth growth and zero depth delta for rejected/canceled/exception outcomes. The held-out set falsifies overfitted candidates including “capacity is a power of 2,” “before is boolean,” and “after is a power of 2.” The confidence/filter run reports six falsifications among 49 internally checked invariants. The unfiltered run reports 22 among 107. Daikon's “samples” denominators count internal checks, not distinct operations; do not report them as independent test executions. The raw candidate output and falsifications are retained. Surviving candidates remain hypotheses requiring explicit specification and tests, not inferred contracts.

## Primary tool references

- [FsCheck](https://fscheck.github.io/FsCheck/)
- [Stryker MTP preview invocation](https://stryker-mutator.io/blog/stryker-net-mtp-runner/)
- [Coyote rewriting and test integration](https://microsoft.github.io/coyote/how-to/unit-testing/)

## Per-survivor semantic assessment

This follow-up assesses the ten survivors and one uncovered mutant from the original 300-test experiment individually. IDs refer to the preserved original mutation report. The supported domain here is successfully computed, finite jitter in [0,1] with positive intervals and representable date arithmetic. Overflow exceptions are separate from successful next-fire outputs. Zero-jitter arithmetic and stochastic jitter are separate obligations.

| ID / line | Classification | Semantic reason and disposition |
|---|---|---|
| 22 / 101 | Actionable: exact boundary | `candidate >= now` returns `now` when `last + interval == now`. Required next fire is strictly later. Added exact two-tick boundary test expecting last+4. |
| 31 / 125 | Actionable stochastic obligation; unclosed | `jitter > 0` returns the unjittered base for every positive fraction. Bounds-only tests cannot distinguish disabling jitter. The API describes spreading occurrences, but has no injectable RNG or prescribed finite-sample distribution. A test demanding variation in N random draws can fail valid code. Keep this as a stochastic adequacy gap; a separately approved RNG seam would permit deterministic positive/negative draw tests. |
| 32 / 125 | Contract-equivalent successful outputs | Changing `jitter <= 0` to `< 0` runs the RNG branch for zero jitter, but `interval * 0 * fraction` truncates to zero; baseFire is already future, so returned timestamp is unchanged. It consumes a shared random draw and costs more, which are not specified observable cadence outputs. |
| 33 / 125 | Actionable stochastic obligation; unclosed | Negating `jitter <= 0` has the same positive-jitter suppression as 31. Zero jitter still produces baseFire through multiplication by zero. Same deterministic RNG-seam limitation as 31. |
| 34 / 126–128 | Contract-equivalent successful outputs | Removing the early return only affects zero jitter in the supported finite domain; the remainder returns exactly baseFire. Extra RNG consumption/performance is not a scheduling timestamp contract. |
| 39 / 134 | Contract-equivalent bounds; exact distribution unclaimed | Subtracting the offset instead of adding reflects the sampled signed offset. Both satisfy the same documented symmetric deviation bounds and future clamp. No API specifies per-draw RNG mapping. The discrete generator's endpoint probabilities are not asserted equivalent; a specified distribution/seed mapping would make that a separate obligation. |
| 41 / 141 | Actionable: equality clamp | `< now` lets `jittered == now` escape. Added a two-tick interval, full jitter, last=100 and now=101, repeated 1,024 times. Each assertion (strictly future) holds for every valid production random draw; a negative offset of one tick exposes the mutant. Mutation detection is sampled, but the test cannot falsely fail valid code because a particular draw did not occur. |
| 45 / 143 | Actionable conditional clamp preference; unclosed | Replacing the ternary condition with false returns now+1 for clamped draws instead of the documented baseFire. When baseFire-now=1 they coincide. When the gap exceeds one, now+1 is also a legitimate *unclamped* negative-jitter output, so public-output bounds cannot identify whether the clamp branch ran. Exact validation requires control/observation of the RNG draw. No artificial private-method inputs or distribution assertions are introduced. |
| 46 / 143 | Actionable conditional clamp preference; unclosed | `baseFire < now` is false for successfully calculated baseFire, so this is the same observable clamp alteration as 45. Same RNG-control limitation. |
| 47 / 143 | Equivalent on reachable state | `baseFire >= now` and `baseFire > now` agree because ComputeBaseFire has already returned a timestamp strictly after now. If date arithmetic overflows, execution throws before this branch. |
| 48 / 143 | Unreachable on supported successful path | The now+1 fallback is reached only if baseFire<=now, excluded by ComputeBaseFire's strict-future postcondition. Changing it to now-1 is not coverable through supported public successful inputs; no test manufactures an invalid private baseFire. |

The two added tests are in `src/Bifrost.Tests.Scheduling/IntervalMutationAdequacyTests.cs`. They do not replace existing tests. The native test output is `results/mutation-adequacy-tests.log` (two discovered, two passed). Stochastic obligations 31/33/45/46 remain explicit gaps, not waived as equivalent or reported as killed. The distinction matters: random repeated universal bounds assertions are sound; finite-sample distribution assertions can reject a conforming implementation and are deliberately absent.

The follow-up repeated the same Stryker command against this baseline plus the two new tests, discovering 302 tests. `results/mutation-adequacy-report.json` identifies 22 as killed by `ExactNextBoundary_AdvancesOneWholeInterval` and 41 as killed by `JitterAtTickBoundary_AlwaysRemainsStrictlyFuture`. This proves the requested specific kills, not a general score improvement.

The aggregate remained 21 killed / 10 survived / one uncovered: two other mutants changed from killed to survived. Mutant 36 (line 132, replacing multiplication by 2 with division by 2) skews random offsets into the negative half; it is an actionable but unclosed stochastic-distribution obligation, just like 31/33, because every resulting successful timestamp still satisfies the universal bounds/clamp contract. Mutant 44 (line 143, ternary condition replaced by true) is equivalent on supported reachable state: baseFire is already strictly future, so the original ternary also selects it. Its earlier kill does not establish a semantic defect. These observed status changes reinforce the preview runner's warning and the effect of random draws; no assertion has been weakened and no aggregate score increase is claimed. Preserve both reports rather than rewriting the initial evidence. Exact causes of the earlier 36/44 kill attribution were not isolated and remain uncertain.
