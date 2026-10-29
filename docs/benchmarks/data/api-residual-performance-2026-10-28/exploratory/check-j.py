from pathlib import Path
import os,subprocess
r=Path('/private/tmp/bifrost-residual')
for mode,blocks in [('batch-sparse',3),('batch',1)]:
 for block in range(blocks):
  for slot,name in enumerate(['original','prior','j','j','prior','original']):
   with (r/f'check-j-{mode}-{block}-{slot}-{name}.txt').open('w') as f:subprocess.run(['dotnet',str(r/name/'Validation.dll'),mode],stdout=f,stderr=subprocess.STDOUT,env={**os.environ,'DOTNET_TieredCompilation':'0'},check=True)
   print(mode,block,slot,name,flush=True)
