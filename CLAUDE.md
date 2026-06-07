# CLAUDE.md

Bifrost is a .NET 10, AOT-compatible work-orchestration library — Channel-based background
execution with autoscaling, Polly resilience, OpenTelemetry, and health checks. Multi-package,
published to NuGet.

## Commands

```bash
dotnet build src/Bifrost.sln -c Release          # solution lives under src/, not repo root
# Tests are TUnit on Microsoft.Testing.Platform (global.json pins the runner) — NOT bare `dotnet test`:
dotnet test --solution src/Bifrost.sln           # full suite
dotnet test --project src/Bifrost.Tests          # single project
dotnet run -c Release --project src/Bifrost.Benchmarks -- --filter "*"   # BenchmarkDotNet
scripts/ci/coverage-gate.sh <cobertura.xml>      # 80% line/branch/method gate
```
> The README's plain `dotnet test` is misleading — with the MTP runner you must pass
> `--solution`/`--project`. Assertions MUST be awaited.

## Toolchain & conventions

- **.NET 10 SDK, preview quality** (`global.json`: `allowPrerelease: true`).
- **`IsAotCompatible=true`** on all shipping projects — keep new code trim/AOT-safe (no unguarded
  reflection or runtime codegen). Test projects opt out.
- **Central Package Management** — bump versions in `src/Directory.Packages.props` only, never in a `.csproj`.
- Versioning via **MinVer** (`v`-prefixed git tags); analyzers/warnings-as-errors flow in from the
  internal `Lvlup.Build` package.

## Layout

- `src/Bifrost{.Core,.HealthChecks,.OpenTelemetry,.Resilience}` — packable libraries.
- Design notes: `docs/designs/`. Benchmarks: `docs/benchmarks/`.
