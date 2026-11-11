# Priority and scaling checks

Status: partial implementation. P1, P2 and A1 have executed boundary checks. History replay integration, cooldown boundaries, A2 lifecycle and review remain outstanding.

Source baseline: `0fc121384950d964722a8de36d72a26679a428d3`.

## Executed checks

`dotnet run --project src/Bifrost.Tests/Bifrost.Tests.csproj -- --treenode-filter '/*/*/PriorityScalingInvariantTests/*'`

Three tests passed, none skipped, reported test duration 647 ms. Build requires network and worker access unavailable in the restricted sandbox.

- P1: 288 class/timestamp cases across four frequencies and six exactly representable quarter-second boost windows. BigInteger rational conversion provides expected keys. Timestamp translation checks 288 follow-up cases. The selected domain avoids rounding ambiguity and overflow. Arbitrary fractional windows remain untested.
- P2: 90 capacity/watermark configurations, independent decimal floor thresholds, occupancy sweeps including threshold equality, and urgency implications. Capacity values include 1, 2, 3 and powers of two plus or minus one. Watermarks are positive, as required by PriorityDispatchOptions. An initial zero-watermark test was rejected by the valid-input guard and corrected; it was not a product defect.
- A1: 810 combinations of initial worker counts, step sizes, current workers and exact/adjacent binary watermark values. An independent signed movement table checks action and target count. Cooldown is zero in this family. Coordinator substitutes supply only current counts; the real engine computes decisions.

The new test class is internal. A directory-scoped SA1600 setting permits the user's requested comment-free test code. No production diagnostics were disabled.

## Production mutation checks

Three temporary mutations each caused the selected test to fail (one selected test, zero passes, exit 2): reversed priority boost sign (P1), Interactive mapped to Batch admission threshold (P2), and inclusive high-watermark comparison (A1). The adjacent `*-mutation.txt` files retain assertion output; `priority-mutations.json` records commands' selected methods and wall times. Each original source was restored in a finally block. The restored suite passed all three tests in 663 ms. These probes establish sensitivity to those specific changes, not to every possible defect.

## A2 baseline failure

`dotnet run --project src/Bifrost.Tests -- --treenode-filter '/*/*/AutoscalingLifecycleInvariantTests/*'`

One test failed (728 ms suite duration). A real timer callback enters the scaling-decision event and blocks on an explicit gate. StopAsync completes before that gate is released. Once released, the callback requests scaling through the coordinator despite the completed stop. This contradicts the interface's completion description (engine has stopped) and the implementation's intended pending-task drain. The replay uses explicit event ordering, not probabilistic sleep. The timer's initial arrival is bounded by a five-second wait and is not a recorded internal thread schedule. This regression remains failing pending a separately reviewed candidate fix. Exact failure output is retained in `autoscaling-stop-baseline.txt`.
