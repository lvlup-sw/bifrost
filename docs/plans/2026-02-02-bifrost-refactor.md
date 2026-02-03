# Bifrost Refactor Plan

**Date:** 2026-02-02
**Type:** Overhaul Refactor
**Reference:** `agentic-engine/docs/decisions/2025-01-31-library-naming.md`

## Overview

Rename Levelup.Channels to Bifrost, restructure to standard lvlup-sw conventions, and set up full CI/CD pipeline with NuGet publishing.

## Tasks

### Phase 1: Directory Restructuring

#### Task 1.1: Create src/ directory structure
- Create `src/` directory
- Move `Directory.Build.props` to `src/`
- Move `Directory.Packages.props` to `src/`
- Create `src/stylecop.json` (copy from valkyrie pattern)

**Acceptance:** Build files in `src/`, root is clean

---

#### Task 1.2: Move project directories to src/
- Move `Levelup.Channels/` → `src/Levelup.Channels/`
- Move `Levelup.Channels.Core/` → `src/Levelup.Channels.Core/`
- Move `Levelup.Channels.HealthChecks/` → `src/Levelup.Channels.HealthChecks/`
- Move `Levelup.Channels.OpenTelemetry/` → `src/Levelup.Channels.OpenTelemetry/`
- Move `Levelup.Channels.Resilience/` → `src/Levelup.Channels.Resilience/`
- Move `Levelup.Channels.Tests/` → `src/Levelup.Channels.Tests/`
- Move `Levelup.Channels.sln` → `src/Levelup.Channels.sln`

**Acceptance:** All projects in `src/`, solution opens correctly

---

### Phase 2: Rename to Bifrost

#### Task 2.1: Rename project directories
- `src/Levelup.Channels/` → `src/Bifrost/`
- `src/Levelup.Channels.Core/` → `src/Bifrost.Core/`
- `src/Levelup.Channels.HealthChecks/` → `src/Bifrost.HealthChecks/`
- `src/Levelup.Channels.OpenTelemetry/` → `src/Bifrost.OpenTelemetry/`
- `src/Levelup.Channels.Resilience/` → `src/Bifrost.Resilience/`
- `src/Levelup.Channels.Tests/` → `src/Bifrost.Tests/`

**Acceptance:** All directories renamed

---

#### Task 2.2: Rename .csproj files and update contents
- Rename all `.csproj` files to `Bifrost.*.csproj`
- Update `<RootNamespace>` in each csproj
- Update `<AssemblyName>` in each csproj
- Update project references to new paths

**Files:**
```
src/Bifrost/Bifrost.csproj
src/Bifrost.Core/Bifrost.Core.csproj
src/Bifrost.HealthChecks/Bifrost.HealthChecks.csproj
src/Bifrost.OpenTelemetry/Bifrost.OpenTelemetry.csproj
src/Bifrost.Resilience/Bifrost.Resilience.csproj
src/Bifrost.Tests/Bifrost.Tests.csproj
```

**Acceptance:** `dotnet build` succeeds (namespace errors expected)

---

#### Task 2.3: Update namespaces in all .cs files
- Replace `namespace Levelup.Channels` → `namespace Bifrost`
- Replace `using Levelup.Channels` → `using Bifrost`
- Update all derived namespaces (Core, Resilience, etc.)

**Pattern:**
```
Levelup.Channels.Core → Bifrost.Core
Levelup.Channels.Resilience → Bifrost.Resilience
Levelup.Channels.HealthChecks → Bifrost.HealthChecks
Levelup.Channels.OpenTelemetry → Bifrost.OpenTelemetry
```

**Acceptance:** `dotnet build` succeeds, all tests pass

---

#### Task 2.4: Rename solution file and update paths
- Rename `src/Levelup.Channels.sln` → `src/Bifrost.sln`
- Update all project paths in solution file

**Acceptance:** Solution opens in IDE, all projects visible

---

### Phase 3: Build Infrastructure

#### Task 3.1: Update Directory.Build.props with Lvlup.Build
Update `src/Directory.Build.props`:
- Add `Lvlup.Build` package reference
- Add `MinVer` package reference
- Add `Microsoft.SourceLink.GitHub` package reference
- Fix repository URLs (`lvlup-sw/bifrost`)
- Add Source Link configuration
- Add MinVer tag prefix configuration
- Update package metadata (name, description, tags)

**Acceptance:** Build succeeds with Lvlup.Build analyzers

---

#### Task 3.2: Update Directory.Packages.props
Update `src/Directory.Packages.props`:
- Add `Lvlup.Build` version
- Add `MinVer` version
- Add `Microsoft.SourceLink.GitHub` version

**Acceptance:** Package restore succeeds

---

#### Task 3.3: Create stylecop.json
Create `src/stylecop.json` with standard lvlup-sw settings.

**Acceptance:** StyleCop rules applied during build

---

### Phase 4: GitHub Repository Setup

#### Task 4.1: Create GitHub repository
```bash
gh repo create lvlup-sw/bifrost --private --description "Channel-based work orchestration with autoscaling, resilience, and observability"
```

**Acceptance:** Repo exists at github.com/lvlup-sw/bifrost

---

#### Task 4.2: Rename local directory and set up remote
- Rename local directory from `levelup-channels` to `bifrost`
- Add git remote origin
- Push initial state

```bash
cd /home/reedsalus/Documents/code/lvlup-sw
mv levelup-channels bifrost
cd bifrost
git remote add origin git@github.com:lvlup-sw/bifrost.git
git push -u origin main
```

**Acceptance:** Code pushed to GitHub

---

### Phase 5: CI/CD Setup

#### Task 5.1: Create CI workflow
Create `.github/workflows/ci.yml`:
- Build & Test job (TUnit with coverage)
- Coverage Gate job (PR comments)
- Update Baseline job (main branch)

**Reference:** `valkyrie/.github/workflows/ci.yml`

**Acceptance:** CI workflow file exists

---

#### Task 5.2: Create coverage-gate script
Create `scripts/ci/coverage-gate.sh`:
- Parse cobertura XML
- Enforce threshold (80%)
- Generate PR comment markdown

**Reference:** `valkyrie/scripts/ci/coverage-gate.sh`

**Acceptance:** Script exists and is executable

---

#### Task 5.3: Create NuGet publish workflow
Create `.github/workflows/publish.yml`:
- Trigger on version tags (v*)
- Build Release configuration
- Pack NuGet packages
- Push to nuget.org

**Acceptance:** Publish workflow file exists

---

### Phase 6: GitHub Integration

#### Task 6.1: Create labels.yml
Create `.github/labels.yml`:
- Inherit base labels from org
- Add Bifrost-specific scopes:
  - `scope:core` - Core channel infrastructure
  - `scope:autoscaling` - Dynamic worker scaling
  - `scope:resilience` - Polly integration
  - `scope:health-checks` - Health check implementations
  - `scope:opentelemetry` - Metrics and observability
  - `scope:performance` - Performance, allocations

**Acceptance:** Labels file exists

---

#### Task 6.2: Create PR template
Create `.github/PULL_REQUEST_TEMPLATE.md`:
- Summary section
- Changes section
- Test Plan section
- Performance Impact checklist

**Reference:** `valkyrie/.github/PULL_REQUEST_TEMPLATE.md`

**Acceptance:** PR template exists

---

#### Task 6.3: Create project-automation workflow
Create `.github/workflows/project-automation.yml`:
- Label sync
- Auto-triage
- Auto-assign
- Project sync to org board

**Reference:** `lvlup-sw/.github/workflow-templates/`

**Acceptance:** Automation workflow exists

---

#### Task 6.4: Create cliff.toml for changelog generation
Create `.github/cliff.toml`:
- Conventional commits parsing
- Changelog generation config

**Reference:** `valkyrie/.github/cliff.toml`

**Acceptance:** cliff.toml exists

---

### Phase 7: Documentation

#### Task 7.1: Create CHANGELOG.md
Create `CHANGELOG.md`:
- Keep a Changelog format
- Document v0.1.0 (initial extraction from agentic-engine)

**Acceptance:** CHANGELOG.md exists with initial entry

---

#### Task 7.2: Update README.md
Update `README.md`:
- Update package names to Bifrost.*
- Add GitHub badges (build, coverage, nuget)
- Update installation instructions
- Update namespace examples

**Acceptance:** README reflects Bifrost naming

---

#### Task 7.3: Update design documentation
Update `docs/design/channel-orchestrator-library.md`:
- Update any namespace references
- Update package names

**Acceptance:** Design doc reflects Bifrost naming

---

### Phase 8: Validation

#### Task 8.1: Run full test suite
```bash
cd src && dotnet test Bifrost.sln
```

**Acceptance:** All tests pass

---

#### Task 8.2: Verify CI runs successfully
- Push changes to feature branch
- Open PR
- Verify CI workflow runs and passes

**Acceptance:** CI green on PR

---

#### Task 8.3: Test NuGet pack
```bash
cd src && dotnet pack --configuration Release
```

**Acceptance:** NuGet packages created successfully

---

## Task Dependencies

```
Phase 1 (Structure) ─┬─► Phase 2 (Rename) ─► Phase 3 (Build Infra) ─┐
                     │                                               │
                     └───────────────────────────────────────────────┼─► Phase 4 (GitHub)
                                                                     │
Phase 5 (CI/CD) ◄────────────────────────────────────────────────────┘
     │
     ├─► Phase 6 (GitHub Integration)
     │
     └─► Phase 7 (Documentation)
              │
              └─► Phase 8 (Validation)
```

## Execution Order

| Order | Task | Dependencies |
|-------|------|--------------|
| 1 | 1.1 Create src/ structure | None |
| 2 | 1.2 Move projects | 1.1 |
| 3 | 2.1 Rename directories | 1.2 |
| 4 | 2.2 Rename csproj files | 2.1 |
| 5 | 2.3 Update namespaces | 2.2 |
| 6 | 2.4 Rename solution | 2.3 |
| 7 | 3.1 Update Directory.Build.props | 2.4 |
| 8 | 3.2 Update Directory.Packages.props | 3.1 |
| 9 | 3.3 Create stylecop.json | 3.2 |
| 10 | 4.1 Create GitHub repo | None (parallel) |
| 11 | 4.2 Rename dir & push | 3.3, 4.1 |
| 12 | 5.1 Create CI workflow | 4.2 |
| 13 | 5.2 Create coverage-gate | 5.1 |
| 14 | 5.3 Create publish workflow | 5.2 |
| 15 | 6.1 Create labels.yml | 4.2 |
| 16 | 6.2 Create PR template | 4.2 |
| 17 | 6.3 Create project-automation | 4.2 |
| 18 | 6.4 Create cliff.toml | 4.2 |
| 19 | 7.1 Create CHANGELOG.md | 4.2 |
| 20 | 7.2 Update README.md | 2.4 |
| 21 | 7.3 Update design docs | 2.4 |
| 22 | 8.1 Run test suite | 7.2, 7.3 |
| 23 | 8.2 Verify CI | 5.3, 8.1 |
| 24 | 8.3 Test NuGet pack | 8.2 |

## Risk Mitigation

| Risk | Mitigation |
|------|------------|
| Namespace rename breaks tests | Run tests after each rename step |
| Solution file corruption | Verify solution opens after each change |
| Git history loss | Use `git mv` for renames, commit incrementally |
| CI fails on first run | Test locally before pushing |

## Success Criteria

- [ ] All projects renamed to Bifrost.*
- [ ] Directory structure matches lvlup-sw conventions
- [ ] Lvlup.Build referenced and analyzers running
- [ ] GitHub repo created at lvlup-sw/bifrost (private)
- [ ] CI workflow runs and passes
- [ ] NuGet packages can be built
- [ ] All tests pass
- [ ] Documentation updated
