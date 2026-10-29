from pathlib import Path
import os,subprocess,json,re,statistics
import sys
r=Path(sys.argv[1]) if len(sys.argv)>1 else Path('/private/tmp/bifrost-latency');d=r/'stripe-diagnostic';d.mkdir(exist_ok=True);summary={}
for payload in ['value','reference']:
 groups={v:[] for v in ['current','final']}
 for block in range(3):
  for slot,v in enumerate(['current','final','final','current']):
   f=d/f'{payload}-{block}-{slot}-{v}.txt'
   with f.open('w') as out:subprocess.run(['dotnet',str(r/f'stripe-{v}/Stripe.dll'),payload],env={**os.environ,'DOTNET_TieredCompilation':'0'},stdout=out,check=True)
   groups[v].append(float(re.search(r'medianNs=([\d.]+)',f.read_text())[1]))
 summary[payload]={v:{'values':xs,'median':statistics.median(xs)} for v,xs in groups.items()}
json.dump(summary,open(d/'summary.json','w'),indent=2);print(summary,flush=True)
