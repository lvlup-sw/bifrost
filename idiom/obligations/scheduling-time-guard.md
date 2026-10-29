# Keep scheduling clock APIs structurally guarded

The shared policy has eight symbols: four clock properties and four bare Task.Delay overloads. The two production projects both reference BannedApiAnalyzers and link the same policy. Runtime uses TimeProvider at SchedulerServiceCollectionExtensions.cs:104 and ScheduleTickLoop delay sites. Original DR-7 explicitly names these four properties; DateTime.Today/Timer are guard-extension suggestions, not proof the current stated rule is violated. Existing architecture test uses a deliberate UtcNow call. The current-revision result is recorded below.

## Open questions

None

### Investigation Log

#### Evidence and competing explanation (2026-09-17)

Read the cited production path and existing guards at revision 3a17ed0f45dfd64d953712ccda286b0915bef838. Catalog applicability is net10.0 / C# 14; no preview feature required. No unresolved implementation question remains for the narrow source-backed claim.

### Bound result

Five Architecture tests passed under TUnit 1.2.11 / MTP 2.0.2 on net10.0 at the analyzed revision, including the deliberate UtcNow fixture. Existing declared subset is verified; scanner invalid-subject handling is independently violated.
