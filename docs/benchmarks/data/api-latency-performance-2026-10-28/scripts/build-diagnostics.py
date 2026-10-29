from pathlib import Path
import subprocess,os
import sys
r=Path(sys.argv[1]) if len(sys.argv)>1 else Path('/private/tmp/bifrost-latency')
for v,src in [('current',r/'before'),('final',Path.cwd()/'src/Bifrost.Concurrency')]:
 for project,out in [('tools/queue-profile/Profile.csproj',r/f'profile-{v}'),(str(r/'stripe-harness/Stripe.csproj'),r/f'stripe-{v}')]:
  with (r/f'build-{out.name}.txt').open('w') as f:subprocess.run(['dotnet','build',project,'-c','Release',f'-p:QueueSources={src}','-o',str(out)],stdout=f,stderr=subprocess.STDOUT,check=True)
 env={**os.environ,'DOTNET_TieredCompilation':'0','DOTNET_JitDisasm':'*get_Current* *TryDequeue* *EnqueueCore* *TryLockedPop* *PopHeldRoot*','DOTNET_JitStdOutFile':str(r/f'jit-confirm-{v}.txt')}
 subprocess.run(['dotnet',str(r/v/'Validation.dll'),'batch-sparse'],env=env,stdout=subprocess.DEVNULL,check=True)
