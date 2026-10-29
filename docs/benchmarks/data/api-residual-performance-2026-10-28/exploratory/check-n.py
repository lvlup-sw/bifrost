from pathlib import Path
import os,subprocess
r=Path('/private/tmp/bifrost-residual')
for mode,blocks in [('buffered-single',1),('batch-sparse',3),('batch',1)]:
 for block in range(blocks):
  for slot,name in enumerate(['original','prior','n','n','prior','original']):
   dll=r/'n/Validation.dll' if name=='n' else r/'final-run'/name/'Validation.dll'
   env=dict(os.environ)
   if mode.startswith('batch'):env['DOTNET_TieredCompilation']='0'
   else:env.pop('DOTNET_TieredCompilation',None)
   with (r/f'check-n-{mode}-{block}-{slot}-{name}.txt').open('w') as f:subprocess.run(['dotnet',str(dll),mode],stdout=f,stderr=subprocess.STDOUT,env=env,check=True)
   print(mode,block,slot,name,flush=True)
