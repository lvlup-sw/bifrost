from pathlib import Path
import os,subprocess
r=Path('/private/tmp/bifrost-residual')
for mode,blocks in [('batch-sparse',3),('buffered-single',2),('batch',1)]:
 env=dict(os.environ)
 if mode.startswith('batch'):env['DOTNET_TieredCompilation']='0'
 else:env.pop('DOTNET_TieredCompilation',None)
 for block in range(blocks):
  for slot,name in enumerate(['original','prior','b','b','prior','original']):
   with (r/f'check-b-{mode}-{block}-{slot}-{name}.txt').open('w') as f:
    subprocess.run(['dotnet',str(r/name/'Validation.dll'),mode],stdout=f,stderr=subprocess.STDOUT,env=env,check=True)
   print(mode,block,slot,name,flush=True)
