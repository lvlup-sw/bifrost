# Dependency defect review

Reviewed 2026-11-11 against PR #84 (`d4a2a4c`) and the open Bifrost issues.
The implementation and public-API regressions are separate layers above that PR.

| Report | Finding and disposition |
|---|---|
| Dependency register D-001 | Reproduced on PR #84 in all three dispatch strategies. Fixed retirement and the related request-token lifetime defect. [RCA and measurements](worker-retirement.md). |
| [#82](https://github.com/lvlup-sw/bifrost/issues/82) | Historical comparer failure already fixed in the base. Added the exact public-API failure → clear → refill relation with a fresh-queue control. |
| [#53](https://github.com/lvlup-sw/bifrost/issues/53) | The old orchestrator StopAsync API is replaced by DrainAsync/AbortAsync. Current lifecycle state caches the abort task and shutdown token. Added real hosted-service Stop/Dispose overlap and repeated teardown regressions for all dispatch strategies. The original Redis-backed application was not available. |
| [#83](https://github.com/lvlup-sw/bifrost/issues/83) | PR #84 already selects the latest Coalesce occurrence. Its 500-interval regression and the scheduling suite pass. Generic/custom/jittered cadence scans remain O(backlog), as documented in #85. |
| [#58](https://github.com/lvlup-sw/bifrost/issues/58) | The base already pins SourceLink 10.0.403. Current restores do not reproduce the reported SourceLink 8.0.0 NU1902 failure. Separate organization-fixture updates are outside this stack. |
| [#77](https://github.com/lvlup-sw/bifrost/issues/77) | Historical CPQ drain/reference-latency observation remains unassigned to a cause. Worker-loop smoke measurements here do not compare the two CPQ implementations or reproduce the Xeon campaign. No queue optimization is justified by these measurements. |
| [#85](https://github.com/lvlup-sw/bifrost/issues/85) | This stack adds lifecycle evidence, not full P0 qualification. Real durable-store process termination, resource-exhaustion budgets, and release-specific acceptance remain open. |
| [#86](https://github.com/lvlup-sw/bifrost/issues/86) | Durable checkpoint batching is a separate research proposal. No persistence acknowledgment contract changes are made. |
| [#28](https://github.com/lvlup-sw/bifrost/issues/28), [#29](https://github.com/lvlup-sw/bifrost/issues/29) | Deferred algorithm/hardware experiments, not reproduced correctness defects. No SNZI or SIMD change without the requested evidence. |
| [#5](https://github.com/lvlup-sw/bifrost/issues/5) | Deferred event-stream allocation proposal. Changing public event types and interface-subscription behavior is outside the worker-retirement fix. |
| [#45](https://github.com/lvlup-sw/bifrost/issues/45) | Publishing and supply-chain rollout tracking. No credentials, publishing policies, or deployment settings change in this stack. |

Dependency-register entries D-002 through D-005 concern consumer tooling and
deployment infrastructure outside the library’s functional boundaries. They are
excluded from this review and require no Bifrost follow-up. They do not count as
remaining library defects or validation gaps. No private deployment details are
retained here.

## Historical regression details

`ComparerClearRefillRegressionTests.FailedEnqueueThenClearMatchesFreshQueueCapacity`
uses only the public factory and queue API. It checks that the injected comparison
failure actually occurs, preserves the original identity, and leaves Count equal
to snapshot length. After Clear, the queue and a fresh control both accept exactly
eight of ten offers and retain the same items. This directly covers #82 without
changing the already-correct heap implementation. Historically, size was published
before the throwing comparison finished, leaving a phantom slot and over-releasing
capacity during Clear. That historical execution is recorded in #82; it was not
rerun on the old source in this review.

`HostedTeardownRegressionTests.StopOverlappingDisposeAndRepeatedTeardownRemainSafe`
blocks a real handler, starts hosted-service drain, and overlaps disposal. It then
calls hosted-service Stop, Abort, and Dispose again after disposal. All three
strategies terminate with zero workers and a canceled, readable shutdown token.
This protects the current equivalent of #53's disposed-source failure boundary.
It does not recreate the original external background-service failure.

## Validation and limits

The lower layer passed 840 main, 160 concurrency, and 359 scheduling tests.
This layer adds three hosted teardown cases and one clear/refill case; all four pass
on both unchanged PR #84 production code and the fixed stack.
Both classes are included in the maintained invariant lane and catalog.
All 136 cases across 25 invariant suites pass.
NativeAOT retirement passes with patched 10.0.15 runtime/compiler packs.
The installed SDK's default 10.0.11 AOT pack fails NuGet audit; the validation
command uses `-p:RuntimeFrameworkVersion=10.0.15` with audit enabled.

No issue is closed manually. #82 is addressed by the added regression. The
remaining Bifrost research and qualification issues retain their stated limits.
Consumer-only reports D-002 through D-005 are excluded from that remaining work.
