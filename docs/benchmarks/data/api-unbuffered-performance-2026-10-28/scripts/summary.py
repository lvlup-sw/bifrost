from pathlib import Path
import re,statistics,sys,json
r=Path(sys.argv[1]);d={}
for f in sorted(r.glob('*.txt')):
 v=f.stem.split('-',1)[1]
 for line in f.read_text().splitlines():
  if not line.startswith('batch '):continue
  vals=dict(re.findall(r'(\w+)=([\w.]+)',line));key='/'.join(vals[k] for k in ['payload','custom','population','c'])
  for m in ['pairMedianNs','stripeMedianNs']:d.setdefault(key,{}).setdefault(v,{}).setdefault(m,[]).append(float(vals[m]))
for key,vs in d.items():
 if 'True' not in key:continue
 print(key)
 for v,ms in vs.items():print(' ',v,' '.join(f'{m}: {statistics.median(x):.3f} ({min(x):.2f}-{max(x):.2f})' for m,x in ms.items()))
(r/'summary.json').write_text(json.dumps(d,indent=2))
