from pathlib import Path
import json,statistics as st,sys
r=Path(sys.argv[1]) if len(sys.argv)>1 else Path(__file__).parent
summary={}
for mode,keys,metric in [('perf',['population','c','threads'],'Mops'),('batch',['payload','custom','population','c'],'pairMedianNs')]:
 cells={}
 for p in sorted(r.glob(f'retained-{mode}-[0-9]-[0-9]-*.txt')):
  version=p.stem.rsplit('-',1)[1]
  for line in p.read_text().splitlines():
   fields=dict(item.split('=',1) for item in line.split()[1:]);key=' '.join(f'{k}={fields[k]}' for k in keys)
   cells.setdefault(key,{}).setdefault(version,[]).append(float(fields[metric]))
 summary[mode]={key:{v:{'n':len(xs),'mean':st.mean(xs),'median':st.median(xs),'min':min(xs),'max':max(xs),'values':xs} for v,xs in vs.items()} for key,vs in cells.items()}
(r/'summary.json').write_text(json.dumps(summary,indent=2)+'\n')
for mode,cells in summary.items():
 print(mode)
 for key,v in cells.items():
  o,e,c=[v[x]['mean'] for x in ['original','earlier','current']]
  print(f'{key}: O={o:.3f} E={e:.3f} C={c:.3f}; C/E={(c/e-1)*100:+.2f}% C/O={(c/o-1)*100:+.2f}%')
