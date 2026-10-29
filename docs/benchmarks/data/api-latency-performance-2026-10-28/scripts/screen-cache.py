from pathlib import Path
import subprocess,os
import sys
r=Path(sys.argv[1]) if len(sys.argv)>1 else Path('/private/tmp/bifrost-latency');d=r/'cache-screen';d.mkdir(exist_ok=True)
for v in ['plain-inline','pushcache','pushcache-inline']:
 env={**os.environ,'DOTNET_TieredCompilation':'0','DOTNET_JitDisasm':'*PopHeldRoot* *TryLockedPop* *TryLockedPush*','DOTNET_JitStdOutFile':str(r/f'jit-{v}.txt')}
 subprocess.run(['dotnet',str(r/v/'Validation.dll'),'batch-sparse'],env=env,stdout=subprocess.DEVNULL,check=True)
for block in range(3):
 for i,v in enumerate(['current','plain-inline','pushcache','pushcache-inline','pushcache-inline','pushcache','plain-inline','current']):
  with (d/f'batch-sparse-{block}-{i}-{v}.txt').open('w') as f:subprocess.run(['dotnet',str(r/v/'Validation.dll'),'batch-sparse'],env={**os.environ,'DOTNET_TieredCompilation':'0'},stdout=f,check=True)
 print('block',block,flush=True)
for i,v in enumerate(['current','plain-inline','pushcache','pushcache-inline','pushcache-inline','pushcache','plain-inline','current']):
 with (d/f'buffered-single-0-{i}-{v}.txt').open('w') as f:subprocess.run(['dotnet',str(r/v/'Validation.dll'),'buffered-single'],stdout=f,check=True)
 print('buffered-single',i,v,flush=True)
