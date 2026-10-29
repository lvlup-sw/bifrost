from pathlib import Path
import json,statistics as st,sys
r=Path(sys.argv[1]) if len(sys.argv)>1 else Path(__file__).parent
summary={}
for mode,keys,metric in [('perf',['population','c','threads'],'Mops'),('batch',['payload','custom','population','c'],'pairMedianNs'),('batch-sparse',['payload','custom','population','c'],'pairMedianNs'),('dense',['population','c','threads'],'Mops')]:
    cells={}
    for p in sorted(r.glob(f'final-{mode}-[0-9]-[0-9]-*.txt')):
        version=p.stem.rsplit('-',1)[1]
        for line in p.read_text().splitlines():
            fields=dict(item.split('=',1) for item in line.split()[1:])
            key=' '.join(f'{k}={fields[k]}' for k in keys)
            cells.setdefault(key,{}).setdefault(version,[]).append(float(fields[metric]))
    summary[mode]={}
    for key,versions in cells.items():
        summary[mode][key]={v:{'n':len(xs),'mean':st.mean(xs),'median':st.median(xs),'min':min(xs),'max':max(xs),'values':xs} for v,xs in versions.items()}
(r/'summary.json').write_text(json.dumps(summary,indent=2)+'\n')
for mode,cells in summary.items():
    print(mode)
    for key,versions in cells.items():
        if len(versions)!=3: continue
        o,p,n=[versions[v]['mean'] for v in ['original','prior','final']]
        print(f'{key}: O={o:.3f} P={p:.3f} N={n:.3f}; N/P={(n/p-1)*100:+.2f}% N/O={(n/o-1)*100:+.2f}%')
