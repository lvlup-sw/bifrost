import os,subprocess,time
from pathlib import Path
r=Path('/private/tmp/bifrost-profile');d=r/'screen';d.mkdir(exist_ok=True)
for mode in ['batch-sparse','buffered-single']:
 for i,v in enumerate(['current','loops','one','one','loops','current']):
  env=dict(os.environ)
  if mode=='batch-sparse':env['DOTNET_TieredCompilation']='0'
  with (d/f'{mode}-{i}-{v}.csv').open('w') as f:subprocess.run(['dotnet',str(r/f'validation-{v}/Validation.dll'),mode],env=env,stdout=f,check=True)
  print(mode,i,v,flush=True)
