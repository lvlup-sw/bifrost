# Systematic history helpers

These test-only sources are linked into each consumer assembly. They do not ship in production packages. Add this item to a test project under `src/`:

```xml
<Compile Include="../Bifrost.Testing.Systematic/*.cs" Link="Systematic/%(Filename)%(Extension)" />
```

Types live in `Bifrost.Tests.Systematic` and are internal to the consuming assembly. `HistoryCommand(Kind, Id, Value)` uses a synthetic string identifier and a signed 64-bit value. `HistoryCase(Configuration, Commands, Parameters)` separates numeric configuration from optional string parameters. Use only generated synthetic data: no customer payloads, credentials, or personal identifiers.

Generation, domain validation, implementation execution, model prediction, and comparison remain separate component functions. The helpers do not infer component semantics. A comparison returns `HistoryObservation(FailureClass, Expected, Actual)`; an empty failure class means success. Use stable invariant-specific failure classes, not a universal exception class. Unexpected executor exceptions propagate instead of becoming shrink successes.

`ReplayRecord` schema version 1 records source SHA and dirty state, runtime, build, invariant, oracle relation, source and optional followup histories, logical clock ticks, optional zero-based fault command position, actor order or an explicit uncontrolled schedule limitation, expected and actual observations, and a replay command. `ToJson` and `FromJson` preserve that metadata. `Replay(isValid, execute)` validates both histories and invokes the supplied executor; the caller compares the new observation with the saved observation. Consumers must restore their logical clock and fault injection from the record when constructing that executor. Replay is exact only for the recorded sequential or explicitly controlled actor order; a seed cannot reproduce arbitrary runtime scheduling.

`HistoryReducer.Reduce(initial, isValid, evaluate, simplifyValues?, simplifyConfiguration?, maxCandidates = 10000)` tries single-command deletions, then numeric value reductions, then numeric configuration reductions. Default numeric candidates are zero, sign, and half. Custom simplifiers may supply component-specific histories. Validation runs before execution, and only the original exact failure class is accepted. A strictly decreasing lexicographic measure (command count, then sum of absolute numeric values) prevents cycles. This handles `long.MinValue` without overflow. Custom string-only simplifications are not supported by that measure.

The reducer restarts after every accepted candidate. `IsOneMinimalWithinCandidates` means its final pass exhausted precisely these candidate transformations, including caller simplifiers; it does not claim a globally smallest counterexample. A candidate budget exhaustion returns false. Validity and evaluation must be deterministic, and callbacks must not mutate their input histories. The candidate budget counts even invalid or nondecreasing candidates.

## Runnable saved counterexample

From a clean checkout with .NET 10 and restored dependencies:

```sh
dotnet build src/Bifrost.Tests/Bifrost.Tests.csproj -m:1
dotnet run --no-build --project src/Bifrost.Tests -- --treenode-filter '/*/*/HistoryToolsTests/SavedSyntheticCounterexample_ReplaysExecutionAndOracle'
```

The committed JSON is `src/Bifrost.Tests/Systematic/synthetic-fifo-counterexample.json`. The test reads it, validates the commands, executes a deliberately incorrect test-only LIFO implementation, predicts the FIFO result with an independent list model, and verifies the reproduced failure class and expected/actual identifiers. A passing test means the saved synthetic failure was reproduced. This demonstrates executable replay without asserting a production defect. The source SHA describes the base revision; dirty=true explicitly records the added test-only mutant. The fixture is resolved relative to the compiled test source path, so replay needs the source checkout rather than a detached binary distribution.

Run all helper checks:

```sh
dotnet run --no-build --project src/Bifrost.Tests -- --treenode-filter '/*/*/HistoryToolsTests/*'
```

For scheduler consumers, initially restrict generation to positive intervals, zero jitter, representable dates, and at least one missed occurrence. Preserve those predicates during reduction. These helpers deliberately contain no scheduler model and make no claims about concurrent schedule coverage.
