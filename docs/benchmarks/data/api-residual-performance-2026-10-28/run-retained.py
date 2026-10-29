from pathlib import Path
import os,subprocess,sys
r=Path(sys.argv[1]) if len(sys.argv)>1 else Path(__file__).parent
modes=sys.argv[2:] or ['perf','batch']
for mode in modes:
 blocks=3 if mode=='batch-sparse' else 2 if mode=='buffered-single' else 1
 env=dict(os.environ)
 if mode.startswith('batch'):env['DOTNET_TieredCompilation']='0'
 else:env.pop('DOTNET_TieredCompilation',None)
 for block in range(blocks):
  for slot,name in enumerate(['original','earlier','current','current','earlier','original']):
   dll=r/name/'Validation.dll'
   args=['dotnet',str(dll)]+([] if mode=='perf' else [mode])
   p=r/f'retained-{mode}-{block}-{slot}-{name}.txt'
   with p.open('w') as f:subprocess.run(args,stdout=f,stderr=subprocess.STDOUT,env=env,check=True)
   print(p.name,flush=True)
