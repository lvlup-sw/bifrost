# Releases

A maintainer releases Bifrost by pushing a `v*` tag to a commit on the first-parent
history of `main`. Use `vX.Y.Z` for a stable release and `vX.Y.Z-<prerelease>` for a
prerelease. Build metadata (`+...`) and numeric prerelease parts with a leading zero
(`rc.01`) are rejected.

The release runs in two parts.

1. **Buildkite `bifrost-release`.** The controller in lvlup-sw/.github owns this
   pipeline and its commands. It checks the tag, then certifies the tagged commit.
   - A prerelease runs `dotnet-coverage`, with the stress tests included.
   - A stable release also runs `invariants` (seeds x4 and the mutation checks), the
     600-second `soak`, and both NativeAOT smokes.

   It builds once with `BifrostTestHooks=false` and packs with `--no-build`. It then
   writes a release manifest with the SHA-256 of every package. Its last step starts
   `.github/workflows/publish.yml` on the tag.
2. **GitHub Actions `publish.yml`.** This workflow builds nothing. It checks that the
   tag points to the given commit and that the commit is on `main`. It waits for the
   Buildkite build to pass and verifies the manifest and every package with
   `.github/scripts/verify-release-handoff.py`. Then it pushes the packages to nuget.org
   with trusted publishing (OIDC). Symbols are pushed too; a symbols failure does not fail
   the release. Last, the `github-release` job writes the GitHub Release notes with
   git-cliff (`.github/cliff.toml`).

Certification evidence (coverage, invariant summary, soak results) is kept as Buildkite
build artifacts.

If the dispatch fails, a maintainer can start `publish.yml` again by hand. Use the
manifest digest from the Buildkite build annotation. The workflow checks everything
again, and `--skip-duplicate` makes a repeated push do nothing:

```sh
gh workflow run publish.yml --ref <tag> -f tag=<tag> -f commit=<sha> \
  -f buildkite_build_number=<n> -f buildkite_build_id=<uuid> -f manifest_sha256=<digest>
```
