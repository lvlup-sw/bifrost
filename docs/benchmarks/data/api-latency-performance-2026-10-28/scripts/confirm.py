from pathlib import Path
import subprocess,os
import sys
r=Path(sys.argv[1]) if len(sys.argv)>1 else Path('/private/tmp/bifrost-latency');d=r/'confirm';d.mkdir(exist_ok=True)
for mode,blocks in [('batch-sparse',3),('buffered-single',2),('perf',2),('batch',1)]:
 for block in range(blocks):
  order=['original','preprofile','current','final','final','current','preprofile','original'] if mode=='batch-sparse' else ['preprofile','current','final','final','current','preprofile'] if mode!='batch' else ['current','final','final','current']
  for slot,v in enumerate(order):
   env=dict(os.environ)
   if mode.startswith('batch'):env['DOTNET_TieredCompilation']='0'
   else:env.pop('DOTNET_TieredCompilation',None)
   with (d/f'{mode}-{block}-{slot}-{v}.txt').open('w') as f:subprocess.run(['dotnet',str(r/v/'Validation.dll')]+([] if mode=='perf' else [mode]),env=env,stdout=f,stderr=subprocess.STDOUT,check=True)
   print(mode,block,slot,v,flush=True)
