from pathlib import Path
import os,subprocess
import sys
r=Path(sys.argv[1]) if len(sys.argv)>1 else Path('/private/tmp/bifrost-profile')
for v in ['original','earlier','current']:
 for scenario in ['batch-value','throughput-sparse']:
  env={**os.environ,'DOTNET_JitDisasm':'*ThroughputLoop* *TryLockedPop* *PopHeldRoot* *EnqueueCore*','DOTNET_JitStdOutFile':str(r/f'jit-{v}-{scenario}.txt')}
  if scenario.startswith('batch'):env['DOTNET_TieredCompilation']='0'
  subprocess.run(['dotnet',str(r/v/'Profile.dll'),scenario,'1'],env=env,stdout=subprocess.DEVNULL,check=True)
  print('jit',v,scenario,flush=True)
