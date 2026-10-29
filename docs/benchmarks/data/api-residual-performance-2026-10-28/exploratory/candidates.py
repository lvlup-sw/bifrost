from pathlib import Path
import shutil,subprocess,os
r=Path('/private/tmp/bifrost-residual')
base=(r/'before/SubQueue.cs').read_text()
for name in ['a','b','c']:
 folder=r/(name+'-sources');shutil.copytree(r/'before',folder,dirs_exist_ok=True)
 s=base.replace('    internal SubQueuePopStatus PopHeldRoot(', '    [MethodImpl(MethodImplOptions.AggressiveInlining)]\n    internal SubQueuePopStatus PopHeldRoot(')
 if name in ['b','c']:
  s=s.replace('    private void UnbufferedPush(', '    [MethodImpl(MethodImplOptions.NoInlining)]\n    private void UnbufferedPush(')
 if name=='c':
  s=s.replace('BufferedTransaction(element, priority, pop: false, out _, out _);','BufferedPushWithRollback(element, priority);')
  at='    /// <summary>Rolls back a compound buffer operation without invoking user code.</summary>'
  s=s.replace(at,'    // Keep discarded out parameters and transaction setup out of the lock-taking hot path.\n    [MethodImpl(MethodImplOptions.NoInlining)]\n    private void BufferedPushWithRollback(TElement element, TPriority priority)\n        => BufferedTransaction(element, priority, pop: false, out _, out _);\n\n'+at)
 (folder/'SubQueue.cs').write_text(s)
 with (r/f'build-{name}.txt').open('w') as f:
  subprocess.run(['dotnet','build','tools/queue-validation/Validation.csproj','-c','Release',f'-p:QueueSources={folder}','-o',str(r/name)],stdout=f,stderr=subprocess.STDOUT,check=True)
for name in ['prior','a','b','c','c','b','a','prior']:
 i=len(list(r.glob(f'explore-{name}-*.txt')))
 with (r/f'explore-{name}-{i}.txt').open('w') as f:
  subprocess.run(['dotnet',str(r/name/'Validation.dll'),'batch-sparse'],env={**os.environ,'DOTNET_TieredCompilation':'0'},stdout=f,stderr=subprocess.STDOUT,check=True)
 print(name,i,flush=True)
