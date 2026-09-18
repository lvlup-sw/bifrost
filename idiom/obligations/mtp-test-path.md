# Retain the correct MTP test path

README.md:152–155 correctly uses dotnet test --solution; CLAUDE.md assertion that README uses bare dotnet test is stale and refuted. global.json sets Microsoft.Testing.Platform. All test projects reference TUnit centrally. Local architecture run exercises MTP through dotnet run as current CI does. Remote shared action internals and exact hosted run are not established; final state will remain bounded to executed local selection.

## Open questions

None

### Investigation Log

#### Evidence and competing explanation (2026-09-17)

Read the cited production path and existing guards at revision 3a17ed0f45dfd64d953712ccda286b0915bef838. Catalog applicability is net10.0 / C# 14; no preview feature required. No unresolved implementation question remains for the narrow source-backed claim.

### Bound result and infrastructure investigation

The initial SDK command could not bind its local named pipe in the sandbox. This was indeterminate, not test failure. An authorized outside-sandbox rerun of `dotnet test --project src/Bifrost.Tests/Bifrost.Tests.csproj -c Release --no-build --treenode-filter '/*/*/AutoscalingEngineTests/*'` passed 22 tests. A deliberately nonexistent class filter returned exit 8 and zero tests. This verifies that local SDK path and empty-selection rejection; it does not verify remote shared action internals.
