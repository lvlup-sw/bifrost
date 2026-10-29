from pathlib import Path
import subprocess,os
r=Path('/private/tmp/bifrost-unbuffered');d=r/'preservation';d.mkdir(exist_ok=True)
for mode,blocks,order in [('batch-sparse',3,['original','current','current','original']),('perf',2,['preprofile','current','current','preprofile'])]:
 for block in range(blocks):
  for slot,v in enumerate(order):
   dll=Path('/private/tmp/bifrost-latency')/v/'Validation.dll' if v!='current' else r/'current/Validation.dll'
   env=dict(os.environ)
   if mode.startswith('batch'):env['DOTNET_TieredCompilation']='0'
   else:env.pop('DOTNET_TieredCompilation',None)
   with (d/f'{mode}-{block}-{slot}-{v}.txt').open('w') as f:subprocess.run(['dotnet',str(dll)]+([] if mode=='perf' else [mode]),env=env,stdout=f,stderr=subprocess.STDOUT,check=True)
   print(mode,block,slot,v,flush=True)
