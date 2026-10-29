from pathlib import Path
import subprocess,sys
r=Path(sys.argv[1]) if len(sys.argv)>1 else Path(__file__).parent
order=['original','prior','c2','c2','prior','original']
for block in range(3):
 for slot,name in enumerate(order):
  with (r/f'dense-{block}-{slot}-{name}.txt').open('w') as f:
   subprocess.run(['dotnet',str(r/name/'Validation.dll'),'dense'],stdout=f,stderr=subprocess.STDOUT,check=True)
  print(block,slot,name,flush=True)
