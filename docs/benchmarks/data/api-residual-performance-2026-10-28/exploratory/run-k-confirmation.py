from pathlib import Path
import os,subprocess,sys
r=Path(sys.argv[1]) if len(sys.argv)>1 else Path(__file__).parent
order=['original','earlier','prior','final','final','prior','earlier','original']
for mode,blocks in [('batch-sparse',3),('buffered-single',2),('perf',1),('batch',1)]:
 env=dict(os.environ)
 if mode.startswith('batch'):env['DOTNET_TieredCompilation']='0'
 else:env.pop('DOTNET_TieredCompilation',None)
 for block in range(blocks):
  for slot,name in enumerate(order):
   args=['dotnet',str(r/name/'Validation.dll')]+([] if mode=='perf' else [mode])
   p=r/f'final-{mode}-{block}-{slot}-{name}.txt'
   with p.open('w') as f:subprocess.run(args,stdout=f,stderr=subprocess.STDOUT,env=env,check=True)
   print(p.name,flush=True)
