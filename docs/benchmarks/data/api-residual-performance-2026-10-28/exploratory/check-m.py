from pathlib import Path
import os,subprocess
r=Path('/private/tmp/bifrost-residual')
for mode,blocks in [('batch-sparse',3),('buffered-single',1),('batch',1)]:
 for block in range(blocks):
  for slot,name in enumerate(['original','prior','m','m','prior','original']):
   dll=r/'m/Validation.dll' if name=='m' else r/'final-run'/name/'Validation.dll'
   env=dict(os.environ)
   if mode.startswith('batch'):env['DOTNET_TieredCompilation']='0'
   else:env.pop('DOTNET_TieredCompilation',None)
   with (r/f'check-m-{mode}-{block}-{slot}-{name}.txt').open('w') as f:subprocess.run(['dotnet',str(dll),mode],stdout=f,stderr=subprocess.STDOUT,env=env,check=True)
   print(mode,block,slot,name,flush=True)
