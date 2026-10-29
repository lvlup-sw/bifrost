from pathlib import Path
import subprocess,os
import sys
r=Path(sys.argv[1]) if len(sys.argv)>1 else Path('/private/tmp/bifrost-profile');d=r/'confirm';d.mkdir(exist_ok=True)
for mode,blocks in [('batch-sparse',3),('buffered-single',1),('perf',1),('batch',1)]:
 for block in range(blocks):
  order=['original','earlier','current','poploop-guard','poploop-guard','current','earlier','original']
  for slot,v in enumerate(order):
   env=dict(os.environ)
   if mode.startswith('batch'):env['DOTNET_TieredCompilation']='0'
   else:env.pop('DOTNET_TieredCompilation',None)
   with (d/f'{mode}-{block}-{slot}-{v}.txt').open('w') as f:subprocess.run(['dotnet',str(r/f'validation-{v}/Validation.dll')]+([] if mode=='perf' else [mode]),env=env,stdout=f,stderr=subprocess.STDOUT,check=True)
   print(mode,block,slot,v,flush=True)
