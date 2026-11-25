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
```
> The README's plain `dotnet test` is misleading — with the MTP runner you must pass
> `--solution`/`--project`. Assertions MUST be awaited.

PR validation runs on Buildkite, planned by the controller in `lvlup-sw/.github`. GitHub Actions
runs no build on pushes to main. The 80% per-assembly coverage gate is the Buildkite
`dotnet-coverage` suite; there is no local coverage-gate script.

## Toolchain & conventions

- **.NET 10 SDK, preview quality** (`global.json`: `allowPrerelease: true`).
- **`IsAotCompatible=true`** on all shipping projects — keep new code trim/AOT-safe (no unguarded
  reflection or runtime codegen). Test projects opt out.
- **Central Package Management** — bump versions in `src/Directory.Packages.props` only, never in a `.csproj`.
- Versioning via **MinVer** (`v`-prefixed git tags); analyzers/warnings-as-errors flow in from the
  internal `Lvlup.Build` package (2.x, owned by Hierophant).
- **ConfigureAwait is automatic.** `Lvlup.Build` 2.x rewrites every implicit await to
  `ConfigureAwait(false)` at build time and suppresses VSTHRD111. Do not add `.ConfigureAwait(false)`.
  Keep explicit calls only where they change behavior: `ConfigureAwaitOptions` overloads, and
  `.WithCancellation(ct).ConfigureAwait(false)` (a bare `WithCancellation` captures the context).
- **No comments inside function bodies** (`comments/no-function-body`, enforced by `lvlup-policy`
  in the Buildkite `conventions` job). Put the explanation on the enclosing declaration (XML `<remarks>` or a comment
  above it) or express it in code. Check locally with `dotnet tool restore && bash scripts/check-conventions.sh`
  (or `scripts/check-conventions.sh pr origin/main` to match the PR job).
  The baseline in `verification/policy/comment-baseline.json` may only shrink.

## Layout

- `src/Bifrost{.Core,.HealthChecks,.OpenTelemetry,.Resilience}` — packable libraries.
- Design notes: `docs/designs/`. Benchmarks: `docs/benchmarks/`.

## Pull requests

When you create or update a pull request, follow `.claude/skills/write-pr/SKILL.md`.
Use only the five headings in the org template, `.github/PULL_REQUEST_TEMPLATE.md` in `lvlup-sw/.github`.
Write the body in Simple English.
Keep the body short.
