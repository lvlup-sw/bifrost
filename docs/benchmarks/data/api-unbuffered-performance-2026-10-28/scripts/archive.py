from pathlib import Path
import json,hashlib,difflib,shutil
r=Path('/private/tmp/bifrost-unbuffered');d=Path('docs/benchmarks/data/api-unbuffered-performance-2026-10-28');d.mkdir(parents=True,exist_ok=True)
# Preserve every recorded result, including failed/abandoned experiment output.
measurements={str(p.relative_to(r)):p.read_text() for folder in ['screen','screen-more','screen-cache','screen-root','screen-commit','confirm','preservation'] for p in sorted((r/folder).glob('*.txt'))}
(d/'measurements.json').write_text(json.dumps(measurements,indent=2)+'\n')
variants={}
base=(r/'before/SubQueue.cs').read_text()
for p in sorted(r.glob('*-sources/SubQueue.cs')):
 v=p.parent.name.removesuffix('-sources');s=p.read_text()
 variants[v]={'sha256':hashlib.sha256(s.encode()).hexdigest(),'patch':''.join(difflib.unified_diff(base.splitlines(True),s.splitlines(True),fromfile='a/src/Bifrost.Concurrency/SubQueue.cs',tofile='b/src/Bifrost.Concurrency/SubQueue.cs'))}
# The TLS-only control preserves the accessor optimization and removes dispatcher inlining.
p=Path('/private/tmp/bifrost-latency/tls-split-sources/SubQueue.cs');s=p.read_text();variants['tls-only']={'sha256':hashlib.sha256(s.encode()).hexdigest(),'patch':''.join(difflib.unified_diff(base.splitlines(True),s.splitlines(True),fromfile='a/src/Bifrost.Concurrency/SubQueue.cs',tofile='b/src/Bifrost.Concurrency/SubQueue.cs'))}
(d/'experiments.json').write_text(json.dumps(variants,indent=2)+'\n')
provenance={}
for v,p in [('current',r/'before'),('old',Path('/private/tmp/bifrost-latency/before')),('preprofile',Path('/private/tmp/bifrost-profile/before')),('original',Path('/private/tmp/bifrost-validation/baseline'))]:
 provenance[v]={f.name:hashlib.sha256(f.read_bytes()).hexdigest() for f in sorted(p.glob('*.cs'))}
provenance['drivers']={str(p):hashlib.sha256(p.read_bytes()).hexdigest() for p in [Path('tools/queue-validation/Program.cs'),Path('tools/queue-validation/Validation.csproj'),r/'stripe-harness/Program.cs']}
assert provenance['current']=={f.name:hashlib.sha256(f.read_bytes()).hexdigest() for f in sorted(Path('src/Bifrost.Concurrency').glob('*.cs'))}
(d/'source-hashes.json').write_text(json.dumps(provenance,indent=2)+'\n')
verification={p.name:p.read_text() for pattern in ['build-*.txt','restore-*.txt','test-*.txt','aot-*.txt','algorithm-check.txt','trace*-current.txt','quality-current.txt'] for p in r.glob(pattern)}
(d/'verification.json').write_text(json.dumps(verification,indent=2)+'\n')
(d/'jit').mkdir(exist_ok=True)
for p in r.glob('jit-*.txt'):shutil.copy2(p,d/'jit'/p.name)
(d/'scripts').mkdir(exist_ok=True)
for p in r.glob('*.py'):shutil.copy2(p,d/'scripts'/p.name)
shutil.copy2('/private/tmp/bifrost-latency/summarize.py',d/'scripts/summarize-confirmation.py')
(d/'stripe-harness').mkdir(exist_ok=True)
for p in (r/'stripe-harness').glob('*'):
 if p.is_file():shutil.copy2(p,d/'stripe-harness'/p.name)
print('Archived',len(measurements),'raw outputs;',len(variants)-1,'candidate patches; queue source unchanged.')
