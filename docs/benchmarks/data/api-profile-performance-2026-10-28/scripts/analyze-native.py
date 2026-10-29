import json,re,bisect,collections
from pathlib import Path
import sys
r=Path(sys.argv[1])
results={}
import sys
keys=sys.argv[2:] or [p.name.removeprefix('native-map-').removesuffix('.json') for p in sorted(r.glob('native-map-*.json'))]
for v in keys:
 methods=sorted(json.load(open(r/f'native-map-{v}.json'))['methods'],key=lambda x:x['start']);starts=[m['start'] for m in methods]
 nodes=[];stack=[];active=False
 for line in (r/f'native-{v}.txt').read_text().splitlines():
  if line.startswith('Call graph:'):active=True;continue
  if active and line.startswith('Total number'):break
  if not active:continue
  m=re.match(r'^([\s+!|:]*)(\d+) (.+)',line)
  if not m:continue
  depth=len(m[1]);count=int(m[2]);name=m[3];pc=re.search(r'\[0x([0-9a-f]+)',name)
  if pc and '???' in name:
   addr=int(pc[1],16);i=bisect.bisect_right(starts,addr)-1
   if i>=0 and addr<methods[i]['start']+methods[i]['size']:name=methods[i]['name'].split('(')[0].split('generic ')[0].split('instance ')[0]+' +'+str(addr-methods[i]['start'])
  name=re.sub(r' \+\d+$','',name);name=re.sub(r'  \(in .*','',name)
  while stack and stack[-1]['depth']>=depth:stack.pop()
  selected=('g__QueueBatch' in name or 'g__ThroughputTarget' in name) or (stack and stack[-1]['selected'])
  node=dict(depth=depth,count=count,self=count,name=name,selected=bool(selected))
  if stack:stack[-1]['self']-=count
  nodes.append(node);stack.append(node)
 exclusive=collections.Counter();inclusive=collections.Counter()
 for n in nodes:
  if n['selected']:exclusive[n['name']]+=n['self'];inclusive[n['name']]+=n['count']
 total=sum(exclusive.values());results[v]={'total':total,'exclusive':exclusive,'inclusive':inclusive}
 print(v,total)
 for name,n in exclusive.most_common(25):print(f'{n:5d} {100*n/total:6.2f}% {name}')
json.dump(results,open(r/'native-summary.json','w'),indent=2)
