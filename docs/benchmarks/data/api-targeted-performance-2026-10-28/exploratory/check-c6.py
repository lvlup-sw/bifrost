from pathlib import Path
import os,subprocess,sys
r=Path(sys.argv[1]) if len(sys.argv)>1 else Path(__file__).parent
for mode in ['batch-sparse','batch','perf']:
 env=dict(os.environ)
 if mode.startswith('batch'): env['DOTNET_TieredCompilation']='0'
 else: env.pop('DOTNET_TieredCompilation',None)
 for slot,name in enumerate(['final','c6','c6','final']):
  args=['dotnet',str(r/name/'Validation.dll')]+([] if mode=='perf' else [mode])
  with (r/f'c6-check-{mode}-{slot}-{name}.txt').open('w') as f:
   subprocess.run(args,stdout=f,stderr=subprocess.STDOUT,env=env,check=True)
  print(mode,slot,name,flush=True)
