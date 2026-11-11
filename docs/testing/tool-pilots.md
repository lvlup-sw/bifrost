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
