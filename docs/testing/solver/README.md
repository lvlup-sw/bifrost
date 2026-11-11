# Coalesce bitvector trial

Z3 4.16.0 checked the interval formula with signed 64-bit subtraction, division, multiplication and addition. UTC ticks are nonnegative and no greater than 3155378975999999999. Interval is positive and at least one occurrence is due. These bounds exclude overflow in the selected result. Operations do not use unbounded integers.

```sh
z3 -T:20 docs/testing/solver/coalesce-bounded.smt2
z3 -T:10 docs/testing/solver/coalesce-unit-interval.smt2
```

The bounded query covers last/now inputs from 0 through 255 and positive intervals through 16. Inputs are zero-extended into 64-bit operands before arithmetic. The first query asks for a result before last, after now, or at least one interval behind now. Result: `unsat`. This proves the predicate only within these bounds and assumptions. The unit-interval query fixes I=1 and permits the full declared date range; its violation query is also `unsat`.

The second query fixes L=0, N=101, I=1 and asks whether the old 100-entry capped selection differs from the formula. Result: `sat`, latest=101 and old-result=100. The adjacent result files retain output. This matches the production failure reproduced by `SchedulerReplayTests`, whose saved baseline record uses these exact values. The solver encodes arithmetic, not complete C# dispatch behavior or jitter.

The unrestricted-interval query in `coalesce.smt2` reached its 30-second limit. `coalesce-result.txt` retains `timeout`. This is neither proof nor counterexample. The bounded version preserves 64-bit semantics while reducing input bounds; it is not evidence for the unproved general case. The `coalesce-bounded-extended-result.txt` output corresponds to the current zero-extended bounded encoding; the earlier `coalesce-bounded-result.txt` came from equivalent explicit 64-bit range constraints.
