# Honor the consumer clock through the public builder

High confidence on mechanism. WorkOrchestratorBuilder.cs:148–152 explicitly constructs WorkOrchestrator with handler, options, logger and named classifier; it omits timeProvider. WorkOrchestrator.cs:114 accepts the optional provider and :123 falls back to System. :271/:324 read timestamps and :610 reports elapsed time. A DI registration alone cannot affect this explicit construction. This is a proposed library composition contract, not a claim that README already promises fake time for every feature. Existing direct-constructor tests do not refute the omission.

## Open questions

None

### Investigation Log

#### Evidence and competing explanation (2026-09-17)

Read the cited production path and existing guards at revision 3a17ed0f45dfd64d953712ccda286b0915bef838. Catalog applicability is net10.0 / C# 14; no preview feature required. No unresolved implementation question remains for the narrow source-backed claim.

### Evaluation refinement

Expand component coverage to the full replacement composition: WithAutoscaling fake timer fires, cooldown advances exactly at the configured boundary, WorkerRegistry uses provider-created timestamps for ordering, and both DLQ entry paths persist the fake FailedAt instant. These are proposed tests, not executed proof. Existing static UtcNow/Timer behavior establishes that those classes cannot currently honor this proposed contract. Preserve source/binary compatibility with overloads; explicit consumer-selected time is a proposed contract, not a newly invented historical guarantee.
