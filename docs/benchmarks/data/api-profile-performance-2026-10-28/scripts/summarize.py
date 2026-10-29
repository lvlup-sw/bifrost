from pathlib import Path
import re,statistics,json,sys,collections
root=Path(sys.argv[1]);groups=collections.defaultdict(lambda:collections.defaultdict(list))
for f in sorted(root.glob('*.txt')):
 m=re.fullmatch(r'(batch-sparse|buffered-single|perf|batch)-(\d+)-(\d+)-(.+)',f.stem)
 if not m:continue
 for line in f.read_text().splitlines():
  x=dict(re.findall(r'(\w+)=([^ ]+)',line));metric='pairMedianNs' if 'pairMedianNs' in x else 'Mops'
  if metric not in x:continue
  case=','.join([m[1],x.get('payload',''),x.get('custom',''),x.get('population',''),x.get('c',''),x.get('threads','')])
  for measure in [metric]+(['stripeMedianNs'] if metric=='pairMedianNs' else []):groups[case+','+measure][m[4]].append(float(x[measure]))
result={case:{v:{'n':len(values),'median':statistics.median(values),'min':min(values),'max':max(values),'values':values} for v,values in versions.items()} for case,versions in groups.items()}
json.dump(result,open(root/'summary.json','w'),indent=2)
for case,x in result.items():
 print(case,' '.join(f'{v}={m["median"]:.3f} [{m["min"]:.3f},{m["max"]:.3f}] n={m["n"]}' for v,m in x.items()))
