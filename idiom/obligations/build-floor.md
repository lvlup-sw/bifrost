# Preserve the evaluated modern build floor

Post-restore floor.json records net10.0, latest, nullable/implicit usings enabled, NET analyzers true/latest and TreatWarningsAsErrors true in all src/sample projects. The initial pre-restore false values are not a defect: NuGet props had not been generated. Lvlup.Build.props:71–72 sets warnings-as-errors; the Release build has 0 warnings and 0 errors. Shared package versions are declared at src/Directory.Packages.props; samples imports that authority. The docs benchmark driver has no direct package references and is not evidence of CPM drift. Verification is local build/evaluation only; remote branch-protection configuration is unknown.

## Open questions

None

### Investigation Log

#### Evidence and competing explanation (2026-09-17)

Read the cited production path and existing guards at revision 3a17ed0f45dfd64d953712ccda286b0915bef838. Catalog applicability is net10.0 / C# 14; no preview feature required. No unresolved implementation question remains for the narrow source-backed claim.
