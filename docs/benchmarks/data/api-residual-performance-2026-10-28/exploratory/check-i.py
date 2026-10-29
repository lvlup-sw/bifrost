from pathlib import Path
import os,subprocess
r=Path('/private/tmp/bifrost-residual')
for mode,blocks in [('batch-sparse',3),('buffered-single',1),('batch',1)]:
 order=['original','prior','i','i','prior','original'] if mode!='buffered-single' else ['original','earlier','prior','i','i','prior','earlier','original']
 env=dict(os.environ)
 if mode.startswith('batch'):env['DOTNET_TieredCompilation']='0'
 else:env.pop('DOTNET_TieredCompilation',None)
 for block in range(blocks):
  for slot,name in enumerate(order):
   with (r/f'check-i-{mode}-{block}-{slot}-{name}.txt').open('w') as f:subprocess.run(['dotnet',str(r/name/'Validation.dll'),mode],stdout=f,stderr=subprocess.STDOUT,env=env,check=True)
   print(mode,block,slot,name,flush=True)
