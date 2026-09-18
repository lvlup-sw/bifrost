# Control behavioral time through TimeProvider

High confidence. AutoscalingEngine.cs:87 creates Timer; :192 reads UtcNow to gate cooldown. WithAutoscaling registers it at AutoscalingExtensions.cs:73. WorkerInfo.cs:41 records UtcNow; WorkerRegistry.cs:141 sorts by CreatedAt. DeadLetterHandler.cs:95 and RejectionRoutingOrchestrator.cs:271 stamp persisted FailedAt data. Their later :105/:278 calls stamp events and were excluded by refutation. DeadLetterQueueExtensions.cs:87 and RejectionRoutingRegistration.cs:57 wire these handlers/decorators. Unlike purely observational logs, these values govern cooldown/order or escape as persisted queue records.

Do not count six EventStreamOrchestrator timestamps or WorkerRegistry telemetry timestamp as defects merely because they use wall time; those observational-only sites need an explicit design choice. G1 proposes named, owned exceptions if the Bifrost-wide policy includes them.

Availability: TimeProvider reference APIs are present at net10.0; Microsoft Learn TimeProvider overview validates CreateTimer/FakeTimeProvider. Existing WorkOrchestrator and scheduler use the replacement. One-site rewrites alone leave the class open.

## Open questions

None

### Investigation Log

#### Evidence and competing explanation (2026-09-17)

Read the cited production path and existing guards at revision 3a17ed0f45dfd64d953712ccda286b0915bef838. Catalog applicability is net10.0 / C# 14; no preview feature required. No unresolved implementation question remains for the narrow source-backed claim.

### Final proof refinement

The rung-3 claim excludes named APIs only. TimeProvider.System can bypass injected behavior without these APIs; clock-through-di now assigns the distinct behavior/identity proof to public-component tests across every named path.
