from pathlib import Path
import os,subprocess
r=Path('/private/tmp/bifrost-residual')
for block in range(2):
 for slot,name in enumerate(['prior','d','e','old-harness','old-harness','e','d','prior']):
  dll = '/private/tmp/bifrost-targeted/final/Validation.dll' if name=='old-harness' else str(r/name/'Validation.dll')
  with (r/f'check-de-{block}-{slot}-{name}.txt').open('w') as f:
   subprocess.run(['dotnet',dll,'batch-sparse'],env={**os.environ,'DOTNET_TieredCompilation':'0'},stdout=f,stderr=subprocess.STDOUT,check=True)
  print(block,slot,name,flush=True)
