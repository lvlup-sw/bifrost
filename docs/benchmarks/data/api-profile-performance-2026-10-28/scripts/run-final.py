from pathlib import Path
import subprocess,os
import sys
r=Path(sys.argv[1]) if len(sys.argv)>1 else Path('/private/tmp/bifrost-profile');d=r/'final-confirm';d.mkdir(exist_ok=True)
for mode,blocks,order in [('batch-sparse',3,['original','current','final','final','current','original']),('buffered-single',1,['earlier','current','final','final','current','earlier']),('perf',1,['earlier','current','final','final','current','earlier']),('batch',1,['current','final','final','current'])]:
 for block in range(blocks):
  for slot,v in enumerate(order):
   env=dict(os.environ)
   if mode.startswith('batch'):env['DOTNET_TieredCompilation']='0'
   else:env.pop('DOTNET_TieredCompilation',None)
   with (d/f'{mode}-{block}-{slot}-{v}.txt').open('w') as f:subprocess.run(['dotnet',str(r/f'validation-{v}/Validation.dll')]+([] if mode=='perf' else [mode]),env=env,stdout=f,stderr=subprocess.STDOUT,check=True)
   print(mode,block,slot,v,flush=True)
