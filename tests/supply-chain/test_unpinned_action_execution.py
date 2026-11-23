"""Supply-chain execution through action refs that are not commit object ids.

The walker follows the uses parser in actions/runner v2.328.0
PipelineTemplateConverter.cs lines 572-611. A remote ref is sent unchanged to
ResolveActionDownloadInfoAsync (ActionManager.cs lines 666-696). The runner
then extracts that resolved commit and, for a JavaScript action, runs node on
runs.main from that directory (NodeScriptActionHandler.cs lines 80-145).

GitHub's own secure-use guide says a tag can be moved by anyone who can update
the action repository, and that a full commit SHA cannot be swapped without a
hash collision. This harness resolves the workflow ref with git at job time,
extracts that commit, and runs its entrypoint. A later pin to a full SHA keeps
the benign entrypoint after the tag moves. A tag ref runs the replacement.
"""

from __future__ import annotations

import os
import re
import shutil
import subprocess
import tarfile
import tempfile
import textwrap
import unittest
from pathlib import Path

import yaml

REPO_ROOT = Path(__file__).resolve().parents[2]
FULL_SHA = re.compile(r"^[0-9a-fA-F]{40}$")
MISSING = object()


class WorkflowLoader(yaml.SafeLoader):
    pass


for first in list(WorkflowLoader.yaml_implicit_resolvers):
    WorkflowLoader.yaml_implicit_resolvers[first] = [
        (tag, pattern)
        for tag, pattern in WorkflowLoader.yaml_implicit_resolvers[first]
        if tag != "tag:yaml.org,2002:bool"
    ]


def load_workflow(path: Path):
    with path.open(encoding="utf-8") as handle:
        return yaml.load(handle, Loader=WorkflowLoader) or {}


def parse_uses(raw: str):
    """Same split as PipelineTemplateConverter.ConvertToSteps."""
    if raw.startswith("docker://"):
        return ("docker", raw[len("docker://") :])
    if raw.startswith("./") or raw.startswith(".\\"):
        return ("local", raw)
    segments = raw.split("@")
    path_segments = [part for part in re.split(r"[/\\]", segments[0]) if part]
    git_ref = segments[1] if len(segments) == 2 else ""
    if (
        len(segments) != 2
        or len(path_segments) < 2
        or not path_segments[0]
        or not path_segments[1]
        or not git_ref
    ):
        raise AssertionError(
            "Expected format {org}/{repo}[/path]@ref. Actual %r" % (raw,)
        )
    name = "%s/%s" % (path_segments[0], path_segments[1])
    directory = "/".join(path_segments[2:])
    return ("remote", name, git_ref, directory, raw)


def walk_remote_uses(root: Path):
    found = []
    seen = set()

    def add_remote(source: Path, raw: str):
        kind = parse_uses(raw)
        if kind[0] != "remote":
            return
        _, name, git_ref, directory, original = kind
        found.append(
            {
                "source": str(source.relative_to(root)),
                "raw": original,
                "name": name,
                "ref": git_ref,
                "path": directory,
            }
        )

    def local_file(uses_path: str) -> Path | None:
        relative = uses_path[2:].replace("\\", "/")
        candidate = (root / relative).resolve()
        if not str(candidate).startswith(str(root.resolve())):
            raise AssertionError("local uses escapes the repository: %s" % uses_path)
        if candidate.is_dir():
            for name in ("action.yml", "action.yaml"):
                manifest = candidate / name
                if manifest.is_file():
                    return manifest
            return None
        if candidate.is_file():
            return candidate
        return None

    def walk_steps(source: Path, steps):
        for step in steps or []:
            raw = step.get("uses") if isinstance(step, dict) else None
            if not isinstance(raw, str):
                continue
            kind = parse_uses(raw)
            if kind[0] == "local":
                target = local_file(kind[1])
                if target is None:
                    raise AssertionError("missing local action %s from %s" % (raw, source))
                if target.name in ("action.yml", "action.yaml"):
                    walk_action(target)
                else:
                    walk_workflow(target)
            else:
                add_remote(source, raw)

    def walk_action(path: Path):
        real = path.resolve()
        if real in seen:
            return
        seen.add(real)
        document = load_workflow(path)
        runs = document.get("runs") or {}
        walk_steps(path, runs.get("steps"))

    def walk_workflow(path: Path):
        real = path.resolve()
        if real in seen:
            return
        seen.add(real)
        document = load_workflow(path)
        for job in (document.get("jobs") or {}).values():
            if not isinstance(job, dict):
                continue
            raw = job.get("uses")
            if isinstance(raw, str):
                kind = parse_uses(raw)
                if kind[0] == "local":
                    target = local_file(kind[1])
                    if target is None:
                        raise AssertionError("missing reusable workflow %s" % raw)
                    walk_workflow(target)
                else:
                    add_remote(path, raw)
            walk_steps(path, job.get("steps"))

    workflow_dir = root / ".github" / "workflows"
    for path in sorted(workflow_dir.glob("*.yml")) + sorted(workflow_dir.glob("*.yaml")):
        walk_workflow(path)
    return found


def git(repo: Path, *args, stdout=subprocess.PIPE):
    completed = subprocess.run(
        ["git", "-C", str(repo), *args],
        check=True,
        stdout=stdout,
        stderr=subprocess.PIPE,
        text=True,
    )
    return completed.stdout


def init_action_repo() -> Path:
    repo = Path(tempfile.mkdtemp(prefix="action-ref-"))
    git(repo, "init", "-b", "main")
    git(repo, "config", "user.email", "supply-chain-test@example.com")
    git(repo, "config", "user.name", "supply-chain-test")
    return repo


def commit_entrypoint(repo: Path, payload: str) -> str:
    (repo / "action.yml").write_text(
        textwrap.dedent(
            """\
            name: stand-in
            runs:
              using: node20
              main: index.js
            """
        ),
        encoding="utf-8",
    )
    (repo / "index.js").write_text(
        'const fs = require("fs");\n'
        'fs.writeFileSync(process.env.SUPPLY_CHAIN_MARKER, "%s");\n' % payload,
        encoding="utf-8",
    )
    git(repo, "add", "action.yml", "index.js")
    git(repo, "-c", "commit.gpgsign=false", "commit", "-m", payload)
    return git(repo, "rev-parse", "HEAD").strip()


def resolve_ref(repo: Path, ref: str) -> str:
    """Job-time resolution of the ref string the runner sends to the service.

    A 40-hex ref is a Git object id. Any other ref is the current tip of that
    tag or branch. rev-parse peels annotated tags to the commit, which is the
    object the runner stores as ResolvedSha.
    """
    if FULL_SHA.fullmatch(ref):
        kind = git(repo, "cat-file", "-t", ref).strip()
        if kind not in ("commit", "tag"):
            raise AssertionError("SHA ref %s is a %s, not a commit" % (ref, kind))
        return git(repo, "rev-parse", "%s^{commit}" % ref).strip()
    return git(repo, "rev-parse", "%s^{commit}" % ref).strip()


def run_entrypoint(repo: Path, resolved: str) -> str:
    """Extract the resolved commit and run node on its runs.main file."""
    with tempfile.TemporaryDirectory(prefix="action-extract-") as extract:
        extract_path = Path(extract)
        archive_path = extract_path / "action.tar"
        git(repo, "archive", "--format=tar", "-o", str(archive_path), resolved)
        with tarfile.open(archive_path) as archive:
            archive.extractall(extract_path, filter="data")
        manifest = load_workflow(extract_path / "action.yml")
        runs = manifest["runs"]
        if runs.get("using") != "node20":
            raise AssertionError("stand-in action is not a node20 action")
        script = extract_path / runs["main"]
        marker = extract_path / "marker.txt"
        env = os.environ.copy()
        env["SUPPLY_CHAIN_MARKER"] = str(marker)
        subprocess.run(
            ["node", str(script)],
            cwd=extract_path,
            env=env,
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )
        return marker.read_text(encoding="utf-8")


def marker_after_maintainer_moves_tag(ref: str) -> str:
    """Publish a benign commit, then point the mutable name at an attacker commit.

    A full SHA stays on the benign object. A tag or branch name is updated with
    git update-ref, then resolved again, then executed.
    """
    repo = init_action_repo()
    try:
        benign = commit_entrypoint(repo, "benign")
        attacker = commit_entrypoint(repo, "attacker")
        if benign == attacker:
            raise AssertionError("the two action commits are the same object")
        if FULL_SHA.fullmatch(ref):
            git(repo, "update-ref", "refs/tags/decoy", attacker)
            resolved = resolve_ref(repo, benign)
        else:
            git(repo, "update-ref", "refs/tags/%s" % ref, benign)
            git(repo, "update-ref", "refs/tags/%s" % ref, attacker)
            resolved = resolve_ref(repo, ref)
        return run_entrypoint(repo, resolved)
    finally:
        shutil.rmtree(repo)


def effective_permissions(workflow: dict, job: dict):
    job_permissions = job.get("permissions", MISSING)
    if job_permissions is not MISSING:
        return job_permissions
    return workflow.get("permissions", MISSING)


def can_push_commits_or_tags(permissions) -> bool:
    """True when the job token is not pinned below contents write.

    Workflow syntax gives an omitted permissions key the enterprise, org, or
    repository default. write-all and contents: write can push commits and tags.
    Setting any explicit map leaves omitted scopes at none.
    """
    if permissions is MISSING or permissions is None:
        return True
    if permissions == "write-all":
        return True
    if permissions in ("read-all", {}):
        return False
    if isinstance(permissions, dict):
        return permissions.get("contents") == "write"
    raise AssertionError("unrecognized permissions value: %r" % (permissions,))


class UnpinnedActionExecutionTest(unittest.TestCase):
    def test_walker_reaches_composite_action_and_label_sync(self):
        uses = walk_remote_uses(REPO_ROOT)
        sources = {item["source"] for item in uses}
        self.assertIn(
            ".github/actions/dotnet-build-test-faf3b5c/action.yml",
            sources,
        )
        self.assertIn(".github/workflows/lvlup-label-sync-faf3b5c.yml", sources)
        self.assertGreaterEqual(len(uses), 4)

    def test_harness_tag_move_runs_replacement_and_sha_does_not(self):
        self.assertEqual(marker_after_maintainer_moves_tag("v6"), "attacker")
        pinned = "0123456789abcdef0123456789abcdef01234567"
        self.assertEqual(marker_after_maintainer_moves_tag(pinned), "benign")

    def test_workflow_refs_keep_the_benign_entrypoint_after_tag_move(self):
        uses = walk_remote_uses(REPO_ROOT)
        self.assertGreater(len(uses), 0)
        for item in uses:
            with self.subTest(source=item["source"], uses=item["raw"]):
                marker = marker_after_maintainer_moves_tag(item["ref"])
                self.assertEqual(
                    marker,
                    "benign",
                    "%s uses %s, and moving that ref executed the replacement action"
                    % (item["source"], item["raw"]),
                )

    def test_explicit_contents_read_cannot_push(self):
        self.assertFalse(can_push_commits_or_tags({"contents": "read"}))
        self.assertFalse(can_push_commits_or_tags({}))
        self.assertFalse(can_push_commits_or_tags("read-all"))
        self.assertTrue(can_push_commits_or_tags(MISSING))
        self.assertTrue(can_push_commits_or_tags("write-all"))
        self.assertTrue(can_push_commits_or_tags({"contents": "write"}))

    def test_ci_workflow_token_cannot_push_commits_or_tags(self):
        document = load_workflow(REPO_ROOT / ".github" / "workflows" / "ci.yml")
        jobs = document.get("jobs") or {}
        self.assertIn("build-test", jobs)
        for name, job in jobs.items():
            with self.subTest(job=name):
                permissions = effective_permissions(document, job)
                shown = "omitted" if permissions is MISSING else permissions
                self.assertFalse(
                    can_push_commits_or_tags(permissions),
                    "ci.yml job %s has no explicit permission below contents write "
                    "(value %r). The token keeps the repository default."
                    % (name, shown),
                )


if __name__ == "__main__":
    unittest.main(verbosity=2)
