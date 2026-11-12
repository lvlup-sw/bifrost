# Exarchos testgen pilot

Reviewed on 2026-11-12 against `3e4044bf96bdb4ae11048384bb97bbcdf0dcfb4d`.
The pilot used Exarchos `feat/testgen-prototype` (`292d52e52e808861b5fc60f26e0ed6c0bfe6622b`) from [Exarchos PR #2193](https://github.com/lvlup-sw/exarchos/pull/2193).
Tools were Bun `1.3.12`, `jq` `1.8.2`, .NET SDK `10.0.303`, and .NET runtime `10.0.11` on Linux x64.
Structured JSON responses, requests, and trial summaries live in [evidence/testgen-pilot](evidence/testgen-pilot/).

## Coalesce calibration replay

The calibration replayed `generate_property_tests` and `generate_metamorphic_tests` on two disposable Bifrost checkouts.
The fixture binding from Exarchos `tests/support/testgen/CoalesceBinding.cs` is archived in [CoalesceBinding.cs](evidence/testgen-pilot/CoalesceBinding.cs).
To reproduce the replay, copy [CoalesceBinding.cs](evidence/testgen-pilot/CoalesceBinding.cs) into `src/Bifrost.Tests.Scheduling/` before applying the `.patch` payloads from [coalesce-property.json](evidence/testgen-pilot/coalesce-property.json) and [coalesce-advance.json](evidence/testgen-pilot/coalesce-advance.json).
`CoalesceBinding.cs` covers positive intervals, zero jitter, UTC ticks, and at least one due occurrence.
Because `emitTUnit` runs `CoalesceBinding.IsValid` on both inputs, `advance.json` omits the upper-bound case from `property.json`.

| Suite | Known-good `d4a2a4c` | Historical #83 `0fc1213` |
| --- | --- | --- |
| `TestgenCoalesceProperty` (`coalesce/latest`) | 11 passed | 6 assertion failures out of 11 |
| `TestgenCoalesceAdvance` (`coalesce/advance-one-interval`) | 10 passed | 7 assertion failures out of 10 |

Both checkouts built `src/Bifrost.Tests.Scheduling` in `Release` with zero errors.
Single-case replay of `/*/*/TestgenCoalesceAdvance/Case_5` with `--minimum-expected-tests 1` failed at `CoalesceBinding.Advances`.
Its concrete input at `.fixture.cases[4]` in [coalesce-advance.json](evidence/testgen-pilot/coalesce-advance.json) is `["639299026800000000", "639299088000000000", "600000000"]`.

## New invariant under test

This trial extends catalog entry `P1` for the arithmetic boundary gap in [issue #85](https://github.com/lvlup-sw/bifrost/issues/85).

- **Invariant (`priority/virtual-time-truncation-and-saturation`)**: `PriorityKey.Precompute` truncates each window toward zero in exact integer units, and `PriorityKey.Compute` clamps `EnqueuedAtTicks - Boost(Class)` to `[long.MinValue, long.MaxValue]`.
- **Supported domain**: Five signed 64-bit integers `(enqueuedAtTicks, interactiveWindowTicks, batchWindowTicks, timestampFrequency, workClass)` with `timestampFrequency >= 1`, `workClass` in `0..2`, and converted non-negative windows at most `long.MaxValue`.
- **Baseline commit**: `3e4044bf96bdb4ae11048384bb97bbcdf0dcfb4d`.
- **Generator operation**: `generate_property_tests` (`tunit/1`, contract version `1`).

## Blinded comparison and withheld fault control

An independent reviewer prepared [fault-control.patch](evidence/testgen-pilot/fault-control.patch) (`priority-intermediate-mul-overflow-64bit`) and withheld it until both candidate suites passed on the baseline.
The patch replaces `Int128` multiplication in `PriorityKey.ToTimestampTicks` with a whole-second guard and `unchecked(window.Ticks * frequency) / TimeSpan.TicksPerSecond`.
When `window.Ticks * frequency` exceeds `long.MaxValue` on a valid window, 64-bit overflow wraps the boost.
The fault compiled cleanly and passed all 20 pre-pilot tests in `PriorityKeyTests`, `PriorityArithmeticBoundaryTests`, and `PriorityScalingInvariantTests`.

Two isolated attempts received the same task description, `test-campaign` skill, and repository instructions.
One attempt wrote native TUnit `1.2.11` tests.
The second attempt paired `PriorityKeyBinding.cs` with `generate_property_tests`.

| Measure | Skill + native TUnit | Skill + `generate_property_tests` |
| --- | --- | --- |
| Baseline result | Passed (`93/93`) | Passed (`113/113`) |
| Hand-written C# lines | 926 | 119 |
| Build failures | 0 | 0 |
| Setup or filter failures | 2 | 1 |
| Binding or authoring time | 6.33 min (`379.94 s`) | 7.10 min (`426.14 s`) |
| Review and repair time | 7.20 min (`432.24 s`) | 1.57 min (`93.98 s`) |
| Total binding, review, and repair | 13.54 min (`812.17 s`) | 8.67 min (`520.12 s`) |
| Baseline execution time | 0.25 min (`14.97 s`) | 0.23 min (`13.64 s`) |
| Withheld control result | Killed (`29` assertion failures, `17` formatter `OverflowException`s) | Killed (`26/26` assertion failures) |
| Single-case replay | Passed (`1/1` failed at intended assertion) | Passed (`Case_54`, `1/1` failed at intended assertion) |

The native attempt hit two TUnit `1.2.11` `--treenode-filter` failures because 4-segment method filters cannot select one `[Arguments]` row under `--minimum-expected-tests 1`.
The author refactored every parameterized vector into a separate `[Test]` method to restore single-case replay.
When TUnit `IsEqualTo` formatted extreme differences under the withheld fault, `Math.Abs(actual - expected)` threw `OverflowException` on 17 cases.

When the first generator request passed `"framework": "TUnit"`, the CLI returned `UNSUPPORTED_FRAMEWORK`.
`describe generate_property_tests --json` types `framework` as `string`, whereas dispatch requires lowercase `"tunit"`.
Emitted `Case_1` through `Case_113` methods replayed on the first run and asserted a boolean predicate without diff-formatter overflow.

## Local adoption decision

- **Adopt `generate_property_tests` locally for pure signed-integer (`long[] -> long`) property suites**: It detected the same withheld fault in `8.67 min` versus `13.54 min` (`36%` less effort) and cut review and repair time from `7.20 min` to `1.57 min`.
- **Keep committed tests native to .NET**: Commit the reviewed C# binding and emitted test file so Bifrost builds and runs without Bun or an Exarchos checkout.
- **Keep native TUnit tests as the default elsewhere**: Use handwritten tests for exception contracts, composite inputs, concurrency, lifecycle, persistence, and `generate_metamorphic_tests` on new invariants.

Retained files are `src/Bifrost.Tests/Queues/PriorityKeyBinding.cs`, `src/Bifrost.Tests/Queues/PriorityVirtualTimePropertyTests.cs`, and [priority-property.json](evidence/testgen-pilot/priority-property.json).
Domain scoping also exposed a positive-offset calendar-end overflow in `MissedFirePolicyApplier.Coalesce`, which now caps step counts at the remaining local and UTC range (`IntervalDateBoundaryTests`).
[Exarchos #2192](https://github.com/lvlup-sw/exarchos/issues/2192) tracks qualification on a second application, CLI packaging, and schema fixes.
[Bifrost #85](https://github.com/lvlup-sw/bifrost/issues/85) tracks the remaining durability, overload, and release-risk evidence.
