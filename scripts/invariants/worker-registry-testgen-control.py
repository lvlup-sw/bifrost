import argparse
import json
from pathlib import Path
import re
import subprocess

parser = argparse.ArgumentParser(description='Detect and replay a controlled WorkerRegistry idle-count defect.')
parser.add_argument('--testgen', required=True, help='testgen executable or bundled testgen-cli.js path')
parser.add_argument('--output', type=Path, required=True, help='Directory for raw evidence outside the repository')
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
testgen = ['node', str(Path(args.testgen).resolve())] if args.testgen.endswith('.js') else [args.testgen]
root = Path(__file__).resolve().parents[2]
source=root/'src/Bifrost/Autoscaling/WorkerRegistry.cs'
generated=root/'src/Bifrost.Tests/Systematic/Testgen/WorkerRegistryGeneratedTests.cs'
original=source.read_text(); suite=generated.read_text()
command=['dotnet','run','--project','src/Bifrost.Tests','-c','Release','--','--treenode-filter','/*/*/WorkerRegistryGeneratedTests/*']
try:
 needle='public int IdleWorkerCount => _workers.Values.Count(w => w.IsIdle && !w.StopRequested);'
 assert needle in original
 source.write_text(original.replace(needle,'public int IdleWorkerCount => _workers.Values.Count(w => w.IsIdle);'))
 failed=subprocess.run(command,cwd=root,text=True,capture_output=True,timeout=120)
 output=failed.stdout+failed.stderr
 (args.output / 'worker-control.log').write_text(output)
 assert failed.returncode!=0,output
 records=[json.loads(m) for m in re.findall(r'TESTGEN_REPLAY (\{[^\n]+\})',output)]
 record=next(r for r in records if r['caseId']=='Case_1')
 assert record['signature']=='stateful/stop/state-mismatch',record
 assert record['histories']==[[{'operation':'create','args':'a'},{'operation':'stop','args':'a'}]],record
 fixture=(args.output / 'worker-replay.json'); fixture.write_text(json.dumps(record))
 generated.unlink()
 result=subprocess.run([*testgen,'replay','--input','src/Bifrost.Tests/Systematic/Testgen/worker-registry.json','--fixture',str(fixture)],cwd=root,text=True,capture_output=True,check=True,timeout=30)
 subprocess.run(['git','apply'],cwd=root,input=result.stdout,text=True,check=True)
 replay=subprocess.run(command,cwd=root,text=True,capture_output=True,timeout=120)
 output=replay.stdout+replay.stderr
 (args.output / 'worker-replay.log').write_text(output)
 assert replay.returncode!=0,output
 repeated=json.loads(re.findall(r'TESTGEN_REPLAY (\{[^\n]+\})',output)[0])
 assert repeated['signature']==record['signature'] and repeated['histories']==record['histories'],repeated
 print(json.dumps(record))
finally:
 source.write_text(original)
 generated.write_text(suite)

restored = subprocess.run(command, cwd=root, text=True, capture_output=True, timeout=120)
(args.output / 'worker-restored.log').write_text(restored.stdout + restored.stderr)
assert restored.returncode == 0, restored.stdout + restored.stderr
