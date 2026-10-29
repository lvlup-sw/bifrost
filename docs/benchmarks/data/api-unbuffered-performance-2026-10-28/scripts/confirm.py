from pathlib import Path
import subprocess,os
r=Path('/private/tmp/bifrost-unbuffered');d=r/'confirm';d.mkdir(exist_ok=True)
for mode,blocks,order in [('batch',3,['old','current','comparer-local','comparer-local','current','old']),('batch-sparse',3,['current','comparer-local','comparer-local','current']),('stripe',3,['old','current','comparer-local','comparer-local','current','old']),('buffered-single',2,['current','comparer-local','comparer-local','current']),('perf',2,['preprofile','current','comparer-local','comparer-local','current','preprofile'])]:
 for block in range(blocks):
  for slot,v in enumerate(order):
   dll={'old':Path('/private/tmp/bifrost-latency/current/Validation.dll'),'preprofile':Path('/private/tmp/bifrost-latency/preprofile/Validation.dll')}.get(v,r/v/'Validation.dll')
   if mode=='stripe':dll=r/f'stripe-{v}/Stripe.dll'
   env=dict(os.environ)
   if mode.startswith('batch') or mode=='stripe':env['DOTNET_TieredCompilation']='0'
   else:env.pop('DOTNET_TieredCompilation',None)
   args=['reference'] if mode=='stripe' else [] if mode=='perf' else [mode]
   with (d/f'{mode}-{block}-{slot}-{v}.txt').open('w') as f:subprocess.run(['dotnet',str(dll)]+args,env=env,stdout=f,stderr=subprocess.STDOUT,check=True)
   print(mode,block,slot,v,flush=True)
