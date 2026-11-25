#!/usr/bin/env python3
"""Fetch and verify the Bifrost packages certified by the Buildkite release train.

The controller-owned ``bifrost-release`` Buildkite pipeline in lvlup-sw/.github builds,
certifies and packs a ``v*`` tag, then dispatches ``publish.yml`` with the tag, commit,
build identity and the SHA-256 of its release manifest. This script trusts none of those
inputs on their own. It re-reads the build through the Buildkite REST API, waits for it to
pass, checks every identity the manifest names, downloads the packed files from the pack
job, checks their sizes, digests and nuspec metadata, and only then writes them to the
package directory for ``dotnet nuget push``.

Configuration comes from environment variables so that workflow inputs are never
interpolated into a shell:

- ``RELEASE_TAG``, ``RELEASE_COMMIT``, ``RELEASE_TAG_OBJECT``
- ``RELEASE_BUILD_NUMBER``, ``RELEASE_BUILD_ID``, ``RELEASE_MANIFEST_SHA256``
- ``BUILDKITE_ORG``, ``BUILDKITE_PIPELINE``, ``BUILDKITE_PIPELINE_ID``
- ``BUILDKITE_RELEASE_READ_TOKEN`` (scopes ``read_builds`` and ``read_artifacts``)
- ``PACKAGE_DIR`` (default ``nupkgs``)

The token is only ever sent to the Buildkite API origin. Artifact downloads are redirected
to object storage; the redirect is never followed automatically and the storage request is
sent without an ``Authorization`` header.

Python standard library only.
"""

from __future__ import annotations

import contextlib
import hashlib
import http.client
import io
import json
import os
import pathlib
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
import zipfile
from dataclasses import dataclass
from typing import Any, Callable

API_BASE = 'https://api.buildkite.com/v2'
REPOSITORY = 'lvlup-sw/bifrost'
REPOSITORY_ID = 1148478806
REPOSITORY_URLS = frozenset({'https://github.com/lvlup-sw/bifrost', 'https://github.com/lvlup-sw/bifrost.git'})
MANIFEST_SCHEMA = 'lvlup-bifrost-release-manifest/v1'
MANIFEST_FILE = 'bifrost-release-manifest.json'
PACKAGES_FILE = 'bifrost-packages.json'
ADMISSION_KEY = 'release-admission'
PACK_KEY = 'release-pack'
HANDOFF_KEY = 'release-handoff'
PACKAGE_IDS = (
    'LevelUp.Bifrost',
    'LevelUp.Bifrost.Concurrency',
    'LevelUp.Bifrost.Core',
    'LevelUp.Bifrost.HealthChecks',
    'LevelUp.Bifrost.OpenTelemetry',
    'LevelUp.Bifrost.Resilience',
    'LevelUp.Bifrost.Scheduling',
    'LevelUp.Bifrost.Scheduling.Core',
    'LevelUp.Bifrost.Scheduling.Testing',
)
PACKAGE_KINDS = ('nupkg', 'snupkg')
CERTIFICATION = {
    'stable': frozenset({'dotnet-coverage', 'invariants', 'soak', 'aot-scheduling', 'aot-concurrency'}),
    'prerelease': frozenset({'dotnet-coverage'}),
}
STRESS_SUITE = 'dotnet-coverage'
MAX_PACKAGE_BYTES = 64 * 1024 * 1024
MAX_MANIFEST_BYTES = 1024 * 1024
MAX_API_BYTES = 8 * 1024 * 1024
MAX_NUSPEC_BYTES = 1024 * 1024
MAX_ZIP_ENTRIES = 4096
MAX_ARTIFACT_PAGES = 5
POLL_SECONDS = 15
POLL_DEADLINE_SECONDS = 15 * 60
REQUEST_TIMEOUT_SECONDS = 60
REDIRECT_CODES = frozenset({301, 302, 303, 307, 308})
TERMINAL_STATES = frozenset({'passed', 'failed', 'canceled', 'skipped', 'not_run', 'blocked'})
PENDING_STATES = frozenset({'creating', 'scheduled', 'running', 'canceling', 'failing'})

CORE = r'(0|[1-9][0-9]*)'
TAG_PATTERN = re.compile(rf'v{CORE}\.{CORE}\.{CORE}(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?')
SHA1_PATTERN = re.compile(r'[0-9a-f]{40}')
SHA256_PATTERN = re.compile(r'[0-9a-f]{64}')
UUID_PATTERN = re.compile(r'[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}')
BUILD_NUMBER_PATTERN = re.compile(r'[1-9][0-9]{0,9}')
SLUG_PATTERN = re.compile(r'[a-z0-9][a-z0-9-]{0,99}')
IMAGE_PATTERN = re.compile(r'[a-z0-9][a-z0-9._/:-]{0,255}@sha256:[0-9a-f]{64}')


class VerificationError(Exception):
    """A check failed; the release must not be published."""


def require(condition: bool, message: str) -> None:
    """Raise VerificationError with ``message`` unless ``condition`` holds."""
    if not condition:
        raise VerificationError(message)


def matches(pattern: re.Pattern[str], value: Any) -> bool:
    """Return whether ``value`` is a string that fully matches ``pattern``."""
    return isinstance(value, str) and pattern.fullmatch(value) is not None


def is_int(value: Any) -> bool:
    """Return whether ``value`` is a JSON integer (booleans excluded)."""
    return isinstance(value, int) and not isinstance(value, bool)


def status_of(response: Any) -> int | None:
    """Return the HTTP status of a response or of an HTTPError carrying a redirect."""
    return getattr(response, 'status', None) or getattr(response, 'code', None)


def classify(tag: str) -> str:
    """Return ``stable`` or ``prerelease`` for a release tag, mirroring the historical publish.yml classifier.

    The tag must be ``v`` plus a SemVer core with optional dot-separated prerelease
    identifiers. Build metadata (``+``) and numeric prerelease identifiers with a leading
    zero are rejected.
    """
    require(len(tag) <= 128 and TAG_PATTERN.fullmatch(tag) is not None, f'tag is not a release tag: {tag!r}')
    version = tag[1:]
    if '-' not in version:
        return 'stable'
    identifiers = version.split('-', 1)[1].split('.')
    require(not any(re.fullmatch(r'0[0-9]+', item) for item in identifiers), f'tag has a leading-zero identifier: {tag!r}')
    return 'prerelease'


def canonical(value: Any) -> bytes:
    """Serialize ``value`` the way ``ci.planner.canonical`` in lvlup-sw/.github does."""
    return (json.dumps(value, sort_keys=True, separators=(',', ':'), ensure_ascii=True) + '\n').encode()


def strict_json(raw: bytes) -> Any:
    """Parse JSON, rejecting duplicate keys and non-finite constants."""
    def pairs(items: list[tuple[str, Any]]) -> dict[str, Any]:
        result: dict[str, Any] = {}
        for key, value in items:
            require(key not in result, f'duplicate JSON key: {key!r}')
            result[key] = value
        return result

    def constant(name: str) -> Any:
        raise VerificationError(f'invalid JSON constant: {name}')

    try:
        return json.loads(raw.decode('utf-8'), object_pairs_hook=pairs, parse_constant=constant)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise VerificationError(f'invalid JSON: {error}') from error


@dataclass(frozen=True)
class Inputs:
    """Validated workflow inputs and fixed pipeline identity."""

    tag: str
    commit: str
    tag_object: str
    build_number: int
    build_id: str
    manifest_sha256: str
    organization: str
    pipeline: str
    pipeline_id: str
    release_class: str

    @property
    def version(self) -> str:
        """The package version: the tag without its ``v`` prefix."""
        return self.tag[1:]

    @classmethod
    def from_env(cls, env: dict[str, str]) -> 'Inputs':
        """Read and validate every input from ``env``; fail closed on any malformed value."""
        def value(name: str) -> str:
            raw = env.get(name, '')
            require(raw != '', f'{name} is required')
            return raw

        tag = value('RELEASE_TAG')
        release_class = classify(tag)
        commit = value('RELEASE_COMMIT')
        require(matches(SHA1_PATTERN, commit), 'RELEASE_COMMIT must be 40 lowercase hex characters')
        tag_object = value('RELEASE_TAG_OBJECT')
        require(matches(SHA1_PATTERN, tag_object), 'RELEASE_TAG_OBJECT must be 40 lowercase hex characters')
        number = value('RELEASE_BUILD_NUMBER')
        require(matches(BUILD_NUMBER_PATTERN, number), 'RELEASE_BUILD_NUMBER must be a positive integer')
        build_id = value('RELEASE_BUILD_ID')
        require(matches(UUID_PATTERN, build_id), 'RELEASE_BUILD_ID must be a lowercase UUID')
        digest = value('RELEASE_MANIFEST_SHA256')
        require(matches(SHA256_PATTERN, digest), 'RELEASE_MANIFEST_SHA256 must be 64 lowercase hex characters')
        organization = value('BUILDKITE_ORG')
        pipeline = value('BUILDKITE_PIPELINE')
        require(matches(SLUG_PATTERN, organization) and matches(SLUG_PATTERN, pipeline), 'Buildkite slugs are malformed')
        pipeline_id = value('BUILDKITE_PIPELINE_ID')
        require(matches(UUID_PATTERN, pipeline_id), 'BUILDKITE_PIPELINE_ID is not configured as a lowercase UUID')
        return cls(tag, commit, tag_object, int(number), build_id, digest, organization, pipeline, pipeline_id, release_class)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    """Surface redirects as HTTPError instead of following them, so credentials never leave the API origin."""

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


Opener = Callable[[urllib.request.Request, float], Any]


def default_opener() -> Opener:
    """Return an opener that never follows redirects."""
    opener = urllib.request.build_opener(NoRedirect)
    return lambda request, timeout: opener.open(request, timeout=timeout)


def read_bounded(response: Any, limit: int) -> bytes:
    """Read at most ``limit`` bytes from ``response``; fail if the body is larger.

    A body shorter than its ``Content-Length`` raises ``http.client.IncompleteRead``, a
    transient error the request retry handles, instead of passing a truncated body on.
    """
    length = response.headers.get('Content-Length')
    if length is not None:
        require(length.isdigit() and int(length) <= limit, f'response is larger than {limit} bytes')
    body = response.read(limit + 1)
    require(len(body) <= limit, f'response is larger than {limit} bytes')
    if length is not None and len(body) < int(length):
        raise http.client.IncompleteRead(body, int(length) - len(body))
    return body


def next_link(header: str) -> str | None:
    """Return the ``rel="next"`` URL from an HTTP Link header, if any."""
    for part in header.split(','):
        match = re.fullmatch(r'\s*<([^>]+)>\s*;\s*rel="next"\s*', part)
        if match:
            return match.group(1)
    return None


class Buildkite:
    """Minimal Buildkite REST client for one pipeline's builds and artifacts.

    Transient failures (HTTP 429, 5xx, network errors) are retried a few times with a
    bounded back-off; every other non-200 answer fails verification.
    """

    def __init__(self, inputs: Inputs, token: str, *, api_base: str = API_BASE, opener: Opener | None = None,
                 sleep: Callable[[float], None] = time.sleep, clock: Callable[[], float] = time.monotonic,
                 allow_insecure_storage: bool = False) -> None:
        require(token != '', 'BUILDKITE_RELEASE_READ_TOKEN is required')
        self.inputs = inputs
        self.token = token
        self.api_base = api_base.rstrip('/')
        self.api_origin = urllib.parse.urlsplit(self.api_base)[:2]
        self.opener = opener or default_opener()
        self.sleep = sleep
        self.clock = clock
        self.allow_insecure_storage = allow_insecure_storage

    @property
    def build_path(self) -> str:
        """API path of the dispatched build."""
        i = self.inputs
        return f'/organizations/{i.organization}/pipelines/{i.pipeline}/builds/{i.build_number}'

    def _open(self, url: str, *, authorized: bool) -> Any:
        """Open ``url`` once. Non-2xx answers, redirects included, raise ``HTTPError``."""
        headers = {'Accept': 'application/json', 'User-Agent': 'bifrost-release-verifier'}
        if authorized:
            require(urllib.parse.urlsplit(url)[:2] == self.api_origin, f'refusing to send the token to {url}')
            headers['Authorization'] = f'Bearer {self.token}'
        request = urllib.request.Request(url, headers=headers, method='GET')
        return self.opener(request, REQUEST_TIMEOUT_SECONDS)

    def _retrying(self, what: str, attempt: Callable[[], Any]) -> Any:
        """Run one whole request ``attempt`` (open, status check and body read) with bounded retries.

        HTTP 429, 5xx and network or truncated-body errors are retried with back-off. Every
        other HTTP status and every ``VerificationError`` (size, digest or content checks)
        fails at once. Errors name ``what`` and the status or exception type only, never a
        URL, so a signed storage URL cannot reach the workflow log.
        """
        attempts = 4
        for number in range(1, attempts + 1):
            try:
                return attempt()
            except urllib.error.HTTPError as error:
                with contextlib.closing(error):
                    code = error.code
                    reset = error.headers.get('RateLimit-Reset', '') if error.headers else ''
                if not (code == 429 or code >= 500) or number == attempts:
                    raise VerificationError(f'{what} failed with HTTP {code}') from None
                self.sleep(min(int(reset), 60) if reset.isdigit() else 5 * number)
            except (urllib.error.URLError, http.client.HTTPException, TimeoutError, ConnectionError) as error:
                if number == attempts:
                    raise VerificationError(f'{what} failed: {type(error).__name__}') from None
                self.sleep(5 * number)
        raise VerificationError(f'{what} failed')

    def get_json(self, path_or_url: str) -> tuple[Any, Any]:
        """GET an API resource as strict JSON; return ``(body, headers)``."""
        url = path_or_url if path_or_url.startswith(('http://', 'https://')) else self.api_base + path_or_url

        def attempt() -> tuple[Any, Any]:
            with self._open(url, authorized=True) as response:
                require(status_of(response) == 200, f'GET {url} returned HTTP {status_of(response)}')
                return strict_json(read_bounded(response, MAX_API_BYTES)), response.headers

        return self._retrying(f'GET {url}', attempt)

    def wait_for_build(self) -> dict[str, Any]:
        """Poll the build until it reaches a terminal state and return it.

        The handoff job dispatches publish.yml as its last action, so the build is usually
        still running for a few seconds when this starts.
        """
        deadline = self.clock() + POLL_DEADLINE_SECONDS
        while True:
            build, _ = self.get_json(self.build_path)
            require(isinstance(build, dict), 'build response is not an object')
            state = build.get('state')
            if state in TERMINAL_STATES:
                return build
            require(state in PENDING_STATES, f'build has an unknown state: {state!r}')
            require(self.clock() < deadline, f'build {self.inputs.build_number} did not finish in time (state {state})')
            print(f'Waiting for Buildkite build {self.inputs.build_number} (state {state})', flush=True)
            self.sleep(POLL_SECONDS)

    def list_artifacts(self, job_id: str) -> list[dict[str, Any]]:
        """List every artifact of one job of the build, following pagination on the API origin."""
        url: str | None = self.api_base + f'{self.build_path}/jobs/{job_id}/artifacts?per_page=100'
        artifacts: list[dict[str, Any]] = []
        for _ in range(MAX_ARTIFACT_PAGES):
            if url is None:
                return artifacts
            page, headers = self.get_json(url)
            require(isinstance(page, list), 'artifact listing is not an array')
            artifacts.extend(page)
            url = next_link(headers.get('Link', '') or '')
        require(url is None, 'artifact listing has too many pages')
        return artifacts

    def _storage_location(self, url: str, artifact_id: str) -> str:
        """Ask the API for a fresh storage redirect for one artifact and return its location."""
        try:
            response = self._open(url, authorized=True)
        except urllib.error.HTTPError as error:
            if error.code not in REDIRECT_CODES:
                raise
            with contextlib.closing(error):
                return error.headers.get('Location', '') or ''
        with response:
            raise VerificationError(
                f'artifact {artifact_id} download returned HTTP {status_of(response)}, expected a redirect')

    def download(self, job_id: str, artifact_id: str, limit: int) -> bytes:
        """Download one artifact: resolve the API redirect, then fetch storage without credentials.

        Every attempt asks for a new redirect, because a signed storage URL expires after
        about a minute and a rate-limit back-off can outlast it.
        """
        url = self.api_base + f'{self.build_path}/jobs/{job_id}/artifacts/{artifact_id}/download'

        def attempt() -> bytes:
            location = self._storage_location(url, artifact_id)
            scheme = urllib.parse.urlsplit(location).scheme
            require(scheme == 'https' or (self.allow_insecure_storage and scheme == 'http'),
                    f'artifact {artifact_id} redirect is not https')
            with self._open(location, authorized=False) as storage:
                require(status_of(storage) == 200, f'artifact {artifact_id} storage returned HTTP {status_of(storage)}')
                return read_bounded(storage, limit)

        return self._retrying(f'artifact {artifact_id} download', attempt)


def final_script_jobs(build: dict[str, Any]) -> dict[str, dict[str, Any]]:
    """Map step key to the final attempt of each script job, and require that every final attempt passed.

    A retried job is superseded by the job named in ``retried_in_job_id``; only the last
    attempt of each step counts. The release train has no block, trigger or input steps.
    """
    jobs = build.get('jobs')
    require(isinstance(jobs, list) and bool(jobs), 'build has no jobs')
    final: dict[str, dict[str, Any]] = {}
    for job in jobs:
        require(isinstance(job, dict), 'build job is not an object')
        kind = job.get('type')
        if kind == 'waiter':
            continue
        require(kind == 'script', f'unexpected job type in the release build: {kind!r}')
        if job.get('retried') is True:
            require(matches(UUID_PATTERN, job.get('retried_in_job_id')), 'retried job has no replacement')
            continue
        key = job.get('step_key')
        require(isinstance(key, str) and bool(key), 'script job has no step key')
        require(key not in final, f'step {key} has more than one final job')
        require(matches(UUID_PATTERN, job.get('id')), f'step {key} has a malformed job id')
        require(job.get('state') == 'passed', f'step {key} did not pass (state {job.get("state")!r})')
        final[key] = job
    return final


def verify_build(inputs: Inputs, build: dict[str, Any]) -> dict[str, dict[str, Any]]:
    """Check the build's identity and outcome; return its final script jobs by step key.

    Buildkite reports a tag build's tag name in ``branch``; a ``tag`` field is checked
    too when the API returns one.
    """
    require(build.get('state') == 'passed', f'build {inputs.build_number} did not pass (state {build.get("state")!r})')
    require(build.get('id') == inputs.build_id, 'build id does not match the dispatch')
    require(build.get('number') == inputs.build_number, 'build number does not match the dispatch')
    require(build.get('commit') == inputs.commit, 'build commit does not match the dispatch')
    require(build.get('branch') == inputs.tag, 'build was not created for the release tag')
    if build.get('tag') is not None:
        require(build.get('tag') == inputs.tag, 'build tag does not match the dispatch')
    require(build.get('source') == 'webhook', 'build was not started by the tag webhook')
    require(not build.get('pull_request'), 'build is a pull request build')
    pipeline = build.get('pipeline')
    require(isinstance(pipeline, dict), 'build has no pipeline')
    require(pipeline.get('id') == inputs.pipeline_id, 'build belongs to a different pipeline')
    require(pipeline.get('slug') == inputs.pipeline, 'build pipeline slug does not match')
    jobs = final_script_jobs(build)
    for key in (ADMISSION_KEY, PACK_KEY, HANDOFF_KEY):
        require(key in jobs, f'build has no passed {key} job')
    return jobs


def expected_files(version: str) -> dict[str, tuple[str, str]]:
    """Return the exact package roster for ``version``: file name to (package id, kind)."""
    return {f'{pid}.{version}.{kind}': (pid, kind) for pid in PACKAGE_IDS for kind in PACKAGE_KINDS}


def verify_manifest(inputs: Inputs, raw: bytes, jobs: dict[str, dict[str, Any]]) -> dict[str, Any]:
    """Check the manifest digest, canonical form and every field against the dispatch and the build."""
    require(hashlib.sha256(raw).hexdigest() == inputs.manifest_sha256, 'manifest SHA-256 does not match the dispatch')
    manifest = strict_json(raw)
    require(isinstance(manifest, dict), 'manifest is not an object')
    require(canonical(manifest) == raw, 'manifest is not canonical JSON')
    require(set(manifest) == {'schema', 'repository', 'repository_id', 'tag', 'tag_object', 'commit', 'version',
                              'release_class', 'control_revision', 'image', 'buildkite', 'certification', 'packages'},
            'manifest has unexpected or missing fields')
    require(manifest['schema'] == MANIFEST_SCHEMA, 'manifest schema is not supported')
    require(manifest['repository'] == REPOSITORY, 'manifest repository does not match')
    require(is_int(manifest['repository_id']) and manifest['repository_id'] == REPOSITORY_ID,
            'manifest repository id does not match')
    require(manifest['tag'] == inputs.tag, 'manifest tag does not match')
    require(manifest['tag_object'] == inputs.tag_object, 'manifest tag object does not match the tag in git')
    require(manifest['commit'] == inputs.commit, 'manifest commit does not match')
    require(manifest['version'] == inputs.version, 'manifest version does not match the tag')
    require(manifest['release_class'] == inputs.release_class, 'manifest release class does not match the tag')
    require(matches(SHA1_PATTERN, manifest['control_revision']), 'manifest control revision is malformed')
    require(matches(IMAGE_PATTERN, manifest['image']), 'manifest image is not pinned by digest')

    buildkite = manifest['buildkite']
    require(isinstance(buildkite, dict) and is_int(buildkite.get('build_number')), 'manifest buildkite section is malformed')
    require(buildkite == {
        'organization': inputs.organization,
        'pipeline_slug': inputs.pipeline,
        'pipeline_id': inputs.pipeline_id,
        'build_id': inputs.build_id,
        'build_number': inputs.build_number,
        'pack_job_id': jobs[PACK_KEY]['id'],
        'handoff_job_id': jobs[HANDOFF_KEY]['id'],
    }, 'manifest buildkite identity does not match the build')

    certification = manifest['certification']
    require(isinstance(certification, list), 'manifest certification is not a list')
    suites: set[str] = set()
    for entry in certification:
        require(isinstance(entry, dict) and set(entry) == {'suite', 'job_id', 'native_stress'}, 'certification entry is malformed')
        suite = entry['suite']
        require(isinstance(suite, str) and suite not in suites, f'certification suite is duplicated or malformed: {suite!r}')
        suites.add(suite)
        require(isinstance(entry['native_stress'], bool), f'{suite} native_stress is not a boolean')
        require(entry['native_stress'] == (suite == STRESS_SUITE), f'{suite} native_stress is not as required')
        require(suite in jobs and jobs[suite]['id'] == entry['job_id'], f'{suite} was not certified by a passed job in this build')
    required = CERTIFICATION[inputs.release_class]
    require(suites == required, f'{inputs.release_class} release needs suites {sorted(required)}, got {sorted(suites)}')
    require(set(jobs) == {ADMISSION_KEY, PACK_KEY, HANDOFF_KEY} | suites, f'build ran unexpected steps: {sorted(jobs)}')

    packages = manifest['packages']
    require(isinstance(packages, list), 'manifest packages is not a list')
    roster = expected_files(inputs.version)
    seen: set[str] = set()
    for entry in packages:
        require(isinstance(entry, dict) and set(entry) == {'file', 'id', 'kind', 'size', 'sha256'}, 'package entry is malformed')
        name = entry['file']
        require(isinstance(name, str) and name in roster and name not in seen, f'package is unexpected or duplicated: {name!r}')
        seen.add(name)
        require((entry['id'], entry['kind']) == roster[name], f'{name} id or kind does not match its file name')
        require(is_int(entry['size']) and 0 < entry['size'] <= MAX_PACKAGE_BYTES, f'{name} size is out of bounds')
        require(matches(SHA256_PATTERN, entry['sha256']), f'{name} SHA-256 is malformed')
    require(seen == set(roster), f'manifest is missing packages: {sorted(set(roster) - seen)}')
    return manifest


def local_name(tag: str) -> str:
    """Strip an XML namespace from an element or attribute name."""
    return tag.rsplit('}', 1)[-1]


def read_nuspec(name: str, data: bytes) -> bytes:
    """Return the single root nuspec of a package, with bounded entry count and size."""
    try:
        archive = zipfile.ZipFile(io.BytesIO(data))
    except zipfile.BadZipFile as error:
        raise VerificationError(f'{name} is not a zip archive') from error
    with archive:
        entries = archive.infolist()
        require(len(entries) <= MAX_ZIP_ENTRIES, f'{name} has too many entries')
        nuspecs = [entry for entry in entries if '/' not in entry.filename and entry.filename.endswith('.nuspec')]
        require(len(nuspecs) == 1, f'{name} must contain exactly one root nuspec')
        require(nuspecs[0].file_size <= MAX_NUSPEC_BYTES, f'{name} nuspec is too large')
        try:
            raw = archive.read(nuspecs[0])
        except (zipfile.BadZipFile, OSError) as error:
            raise VerificationError(f'{name} nuspec cannot be read') from error
    require(len(raw) <= MAX_NUSPEC_BYTES, f'{name} nuspec is too large')
    return raw


def verify_nuspec(name: str, data: bytes, package_id: str, kind: str, inputs: Inputs) -> None:
    """Open a package and check its nuspec id, version, repository url and commit, and package type.

    DTDs are refused before parsing, so entity expansion cannot be used against the parser.
    """
    raw = read_nuspec(name, data)
    require(b'<!DOCTYPE' not in raw and b'<!ENTITY' not in raw, f'{name} nuspec declares a DTD')
    try:
        root = ET.fromstring(raw)
    except ET.ParseError as error:
        raise VerificationError(f'{name} nuspec is not valid XML') from error
    require(local_name(root.tag) == 'package', f'{name} nuspec root is not <package>')
    metadata = [child for child in root if local_name(child.tag) == 'metadata']
    require(len(metadata) == 1, f'{name} nuspec has no single <metadata>')
    fields: dict[str, list[ET.Element]] = {}
    for child in metadata[0]:
        fields.setdefault(local_name(child.tag), []).append(child)

    def text(field: str) -> str:
        found = fields.get(field, [])
        require(len(found) == 1, f'{name} nuspec has no single <{field}>')
        return (found[0].text or '').strip()

    require(text('id') == package_id, f'{name} nuspec id is not {package_id}')
    require(text('version') == inputs.version, f'{name} nuspec version is not {inputs.version}')
    repositories = fields.get('repository', [])
    require(len(repositories) == 1, f'{name} nuspec has no single <repository>')
    attributes = {local_name(key): value for key, value in repositories[0].attrib.items()}
    require(attributes.get('url') in REPOSITORY_URLS, f'{name} nuspec repository url is not {REPOSITORY}')
    require(attributes.get('commit') == inputs.commit, f'{name} nuspec repository commit is not {inputs.commit}')
    package_types = {
        package_type.attrib.get('name')
        for group in fields.get('packageTypes', [])
        for package_type in group
        if local_name(package_type.tag) == 'packageType'
    }
    if kind == 'snupkg':
        require(package_types == {'SymbolsPackage'}, f'{name} is not a symbols package')
    else:
        require('SymbolsPackage' not in package_types, f'{name} is marked as a symbols package')


def artifacts_by_path(artifacts: list[dict[str, Any]], job_id: str) -> dict[str, dict[str, Any]]:
    """Index a job's artifacts by path, requiring finished, unique, job-bound entries."""
    indexed: dict[str, dict[str, Any]] = {}
    for artifact in artifacts:
        require(isinstance(artifact, dict), 'artifact is not an object')
        path = artifact.get('path')
        require(isinstance(path, str) and bool(path) and path not in indexed, f'artifact path is duplicated or malformed: {path!r}')
        require(artifact.get('job_id') == job_id, f'artifact {path} belongs to another job')
        require(artifact.get('state') == 'finished', f'artifact {path} is not finished')
        require(matches(UUID_PATTERN, artifact.get('id')), f'artifact {path} has a malformed id')
        indexed[path] = artifact
    return indexed


def verify(inputs: Inputs, client: Buildkite) -> dict[str, bytes]:
    """Run every check and return the verified package bytes by file name."""
    build = client.wait_for_build()
    jobs = verify_build(inputs, build)
    handoff_id = jobs[HANDOFF_KEY]['id']
    pack_id = jobs[PACK_KEY]['id']

    handoff_artifacts = artifacts_by_path(client.list_artifacts(handoff_id), handoff_id)
    require(set(handoff_artifacts) == {MANIFEST_FILE}, f'handoff job must publish only {MANIFEST_FILE}')
    raw_manifest = client.download(handoff_id, handoff_artifacts[MANIFEST_FILE]['id'], MAX_MANIFEST_BYTES)
    manifest = verify_manifest(inputs, raw_manifest, jobs)

    pack_artifacts = artifacts_by_path(client.list_artifacts(pack_id), pack_id)
    entries = {entry['file']: entry for entry in manifest['packages']}
    require(set(pack_artifacts) == set(entries) | {PACKAGES_FILE}, 'pack job artifacts do not match the manifest')

    verified: dict[str, bytes] = {}
    for name in sorted(entries):
        entry = entries[name]
        artifact = pack_artifacts[name]
        require(artifact.get('file_size') == entry['size'], f'{name} artifact size does not match the manifest')
        if artifact.get('sha256sum') is not None:
            require(artifact.get('sha256sum') == entry['sha256'], f'{name} artifact SHA-256 does not match the manifest')
        data = client.download(pack_id, artifact['id'], MAX_PACKAGE_BYTES)
        require(len(data) == entry['size'], f'{name} downloaded size does not match the manifest')
        require(hashlib.sha256(data).hexdigest() == entry['sha256'], f'{name} downloaded SHA-256 does not match the manifest')
        verify_nuspec(name, data, entry['id'], entry['kind'], inputs)
        verified[name] = data
    return verified


def write_packages(directory: pathlib.Path, packages: dict[str, bytes]) -> None:
    """Write verified packages into a new or empty directory."""
    directory.mkdir(parents=True, exist_ok=True)
    require(not any(directory.iterdir()), f'{directory} must be empty')
    for name, data in sorted(packages.items()):
        (directory / name).write_bytes(data)


def summarize(inputs: Inputs, packages: dict[str, bytes]) -> str:
    """Return a Markdown summary of the verified release."""
    lines = [
        f'### Verified Buildkite release {inputs.tag}',
        '',
        f'- Commit `{inputs.commit}` ({inputs.release_class})',
        f'- Buildkite `{inputs.organization}/{inputs.pipeline}` build {inputs.build_number} (`{inputs.build_id}`)',
        f'- Manifest SHA-256 `{inputs.manifest_sha256}`',
        '',
        '| Package | SHA-256 |',
        '| --- | --- |',
    ]
    lines += [f'| `{name}` | `{hashlib.sha256(data).hexdigest()}` |' for name, data in sorted(packages.items())]
    return '\n'.join(lines) + '\n'


def main(env: dict[str, str] | None = None, client_factory: Callable[[Inputs, str], Buildkite] | None = None) -> int:
    """Verify the dispatched release and write its packages; return the process exit code."""
    env = dict(os.environ) if env is None else env
    factory = client_factory or (lambda inputs, token: Buildkite(inputs, token))
    try:
        inputs = Inputs.from_env(env)
        packages = verify(inputs, factory(inputs, env.get('BUILDKITE_RELEASE_READ_TOKEN', '')))
        write_packages(pathlib.Path(env.get('PACKAGE_DIR') or 'nupkgs'), packages)
    except VerificationError as error:
        print(f'::error::Release handoff verification failed: {error}', file=sys.stderr)
        return 1
    summary = summarize(inputs, packages)
    print(summary)
    if env.get('GITHUB_STEP_SUMMARY'):
        with open(env['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as handle:
            handle.write(summary)
    return 0


if __name__ == '__main__':
    sys.exit(main())
