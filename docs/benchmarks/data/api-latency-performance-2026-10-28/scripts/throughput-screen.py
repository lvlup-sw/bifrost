from pathlib import Path
import subprocess,os
import sys
r=Path(sys.argv[1]) if len(sys.argv)>1 else Path('/private/tmp/bifrost-latency');d=r/'throughput-screen';d.mkdir(exist_ok=True)
for mode in ['buffered-single','perf']:
 for i,v in enumerate(['current','outline','outline-partial','inline','inline','outline-partial','outline','current']):
  with (d/f'{mode}-0-{i}-{v}.txt').open('w') as f:subprocess.run(['dotnet',str(r/v/'Validation.dll')]+([] if mode=='perf' else [mode]),stdout=f,check=True)
  print(mode,i,v,flush=True)
