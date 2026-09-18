# Use fake time for elapsed-time assertions

High confidence for cited cases only. AutoscalingEngineTests.cs:281–300 sets a 50ms cooldown then waits 100ms. AutoscalingEngineEventsTests.cs:41,65,127,152,190,232 waits to obtain evaluations. AutoscalingEngineTimerTests.cs:182 sleeps to assert no more evaluations. These are in-process component assertions, not external-clock integration requirements. Worker delegate delays and test watchdogs must be distinguished: use explicit system provider for watchdogs, cancellation/barriers for worker parking. G2 avoids banning legitimate bounded system deadlines.

## Open questions

None

### Investigation Log

#### Evidence and competing explanation (2026-09-17)

Read the cited production path and existing guards at revision 3a17ed0f45dfd64d953712ccda286b0915bef838. Catalog applicability is net10.0 / C# 14; no preview feature required. No unresolved implementation question remains for the narrow source-backed claim.

### Evaluation refinement

Narrow the assigned rung-3 claim to bare overload exclusion and explicit clock selection. It cannot prove that an explicit TimeProvider.System delay is an appropriate watchdog. Causal-time assertions belong to the expanded clock-through-di component obligation; reviewers must reject causal System waits disguised as deadlines. No guarantee of full test determinism is inferred from G2.
