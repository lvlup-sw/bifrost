from pathlib import Path
import subprocess,os
r=Path('/private/tmp/bifrost-unbuffered')
for v in ['current','result','comparer-local']:
 env={**os.environ,'DOTNET_TieredCompilation':'0','DOTNET_JitDisasm':'*TryLockedPop* *MoveDownCustomComparer* *UnbufferedPopHeldRoot*','DOTNET_JitStdOutFile':str(r/f'jit-{v}.txt')}
 with (r/f'jit-process-{v}.txt').open('w') as f:subprocess.run(['dotnet',str(r/v/'Validation.dll'),'batch'],env=env,stdout=f,check=True)
for v,src in [('old',Path('/private/tmp/bifrost-latency/before')),('current',r/'before'),('comparer-local',r/'comparer-local-sources')]:
 with (r/f'build-stripe-{v}.txt').open('w') as f:subprocess.run(['dotnet','build',str(r/'stripe-harness/Stripe.csproj'),'-c','Release',f'-p:QueueSources={src}','-o',str(r/f'stripe-{v}')],stdout=f,stderr=subprocess.STDOUT,check=True)
 print('built diagnostic',v,flush=True)
