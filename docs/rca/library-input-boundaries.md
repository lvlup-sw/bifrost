# Cancellation and input boundaries

Seven regressions failed before the fixes; eleven checks passed afterward.

| Boundary | Reproduction and root cause | Fix |
|---|---|---|
| DLQ cancellation | Cancel `ReadAllAsync`'s token after supplying a separate enumerator token. An item was still removed because the second token replaced the first. | Check both tokens on each synchronous read; no linked CTS or async state machine. |
| Admission correlation | Both enqueue forms lost `CorrelationId` in rejected-work records and notifications. The routing helper supplied null. | Pass producer metadata through the rejection path. |
| HTTP policy | `MaxRetries = -1` silently disabled retry. The optional-strategy branch bypassed Polly validation. | Reject negative counts before pipeline construction. Zero still disables retry. |
| Job identity | `job\n` passed the job-name regex and reached persistence. Regex `$` permits a final newline. | Use absolute beginning/end anchors. |
| Relative schedule | An advancing clock rejected `After(TimeSpan.Zero)` during register and update. Resolution and validation sampled different instants. | Resolve, validate, and compute using one clock sample per mutation. |

NaN jitter was investigated but is **not a defect** on this target: all three
construction/mutation tests passed before any fix because the runtime range guard
rejects NaN. Those checks remain as qualification coverage. The enumerator-token
cancellation control also passed before the fix.

These changes add no new per-item allocations. Clock sampling and policy/name
validation run at registration or configuration boundaries. Completion correlation
is outside this change: `WorkEnqueueOptions.CorrelationId` is admission metadata,
not a promise to propagate handler execution context.
