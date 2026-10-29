"""Aggregate one complete campaign without changing any raw measurements."""
import argparse, collections, csv, json, math, os, pathlib, re, statistics, xml.etree.ElementTree as ET

os.environ.setdefault('MPLCONFIGDIR', '/tmp/bifrost-cpq-mpl')
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt

args = argparse.ArgumentParser()
args.add_argument('campaign', type=pathlib.Path)
args.add_argument('output', type=pathlib.Path)
options = args.parse_args()
root, out = options.campaign, options.output
out.mkdir(parents=True, exist_ok=True)
assert (root/'exit-code.txt').read_text().strip() == '0', 'Campaign did not finish successfully.'

def stats(values):
    assert values and all(math.isfinite(value) and value>0 for value in values), values
    return {'n':len(values), 'mean':statistics.mean(values), 'min':min(values), 'max':max(values)}

def write_csv(name, rows):
    rows = list(rows)
    with (out/name).open('w', newline='') as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]),lineterminator="\n")
        writer.writeheader()
        writer.writerows(rows)

server = collections.defaultdict(list)
for repeat in range(1,5):
    for kind, filename, count in [('scaling','throughput.csv',96),('stickiness','throughput-stickiness.csv',32),('buffered','throughput-buffered-ab.csv',64)]:
        file = root/f'current-{repeat}'/kind/filename
        rows = list(csv.DictReader(line for line in file.read_text(encoding='utf-8-sig').splitlines() if not line.startswith('#')))
        assert len(rows) == count, (file, len(rows))
        for row in rows:
            key=(kind,row['Target'],row['Workload'],int(row['ThreadCount']),int(row['Stickiness']),int(row.get('BufferCapacity',0)))
            server[key].append(float(row['OpsPerSecond']) / 1e6)
server_summary=[dict(zip(['kind','target','workload','threads','stickiness','buffer'],key), **stats(values)) for key,values in sorted(server.items())]
assert len(server_summary)==192 and all(row['n']==4 for row in server_summary)
write_csv('server-summary.csv',server_summary)

def nanoseconds(text):
    value, unit = text.replace(',','').strip().rsplit(' ',1)
    return float(value)*{'ns':1,'us':1000,'μs':1000,'µs':1000,'ms':1e6,'s':1e9}[unit]

def allocated_bytes(text):
    if text.strip() in ['-', '0 B', '0', '0.0 B']:
        return 0.0
    value,unit=text.replace(',','').strip().split(' ')
    return float(value)*{'B':1,'KB':1024,'kB':1024,'MB':1024**2}[unit]

latency=collections.defaultdict(list)
allocations=collections.defaultdict(list)
for kind, expected in [('latency',24),('buffered-latency',16)]:
    for repeat in [1,2]:
        files=list((root/f'{kind}-{repeat}'/'results').glob('*-report.csv'))
        rows=[row for file in files for row in csv.DictReader(file.open(encoding="utf-8-sig"))]
        assert len(rows)==expected, (kind,repeat,len(rows))
        for row in rows:
            key=(kind,row['Method'],int(row['Population']),int(row.get('BufferCapacity',0)))
            latency[key].append(nanoseconds(row['Mean']))
            allocations[key].append(allocated_bytes(row['Allocated']))
latency_summary=[dict(zip(['kind','method','population','buffer'],key),**stats(values),max_allocated_bytes=max(allocations[key])) for key,values in sorted(latency.items())]
assert len(latency_summary)==40 and all(row['n']==2 for row in latency_summary)
write_csv('latency-summary.csv',latency_summary)

validation=collections.defaultdict(list)
validation_alloc=collections.defaultdict(list)
log_name=re.compile(r'validation-(\d+)-(\d+)-(original|current|old|preprofile)-(throughput|buffered-single|batch|batch-sparse|stripe)\.log')
validation_logs=[(file.name,file.read_text()) for file in sorted((root/'logs').glob('validation-*.log'))]
if not validation_logs:
    validation_logs=sorted(json.loads((root/'validation-outputs.json').read_text()).items())
for filename,text in validation_logs:
    match=log_name.fullmatch(filename)
    if not match: continue
    block,slot,version,mode=match.groups()
    family='original-comparison' if int(slot)<=4 else 'optimization-comparison'
    for line in text.splitlines():
        fields=dict(re.findall(r'(\w+)=([^\s]+)',line))
        for metric in ['Mops','pairMedianNs','pairP90Ns','stripeMedianNs','medianNs']:
            if metric not in fields: continue
            key=(family,version,mode,fields.get('payload','value'),fields.get('custom','False'),
                 int(fields.get('population',4096)),int(fields.get('c',0)),int(fields.get('threads',1)),metric)
            validation[key].append(float(fields[metric]))
            validation_alloc[key].append(int(fields['allocated']))
validation_summary=[dict(zip(['family','version','mode','payload','custom','population','buffer','threads','metric'],key),**stats(values),max_allocated_bytes=max(validation_alloc[key])) for key,values in sorted(validation.items())]
assert len(validation_summary)==325 and all(row['n']==6 for row in validation_summary)
write_csv('validation-summary.csv',validation_summary)

# Match same-run target ratios before aggregating, rather than dividing grand means.
speedups=[]
for key,values in sorted(server.items()):
    kind,target,workload,threads,sticky,buffer=key
    if kind=='scaling' and target=='MultiQueueRelaxed':
        baseline=server[(kind,'LockingBaseline',workload,threads,sticky,buffer)]
        speedups.append({'workload':workload,'threads':threads,**stats([a/b for a,b in zip(values,baseline)])})
write_csv('speedup-summary.csv',speedups)

workloads=['UniformMixed5050','SplitProducerConsumer','NarrowKeyRange','Drain']
workload_names=['Uniform mixed 50/50','Split producer / consumer','Narrow key range','Drain']
threads=[1,2,4,8,16,32,64,128]
palette={'MultiQueueRelaxed':'#2a78d6','LockingBaseline':'#eb6834','MultiQueueDequeueMin':'#1baf7a'}
target_names={'MultiQueueRelaxed':'Relaxed MultiQueue','LockingBaseline':'Synchronized BCL','MultiQueueDequeueMin':'Best-observed scan'}
markers={'MultiQueueRelaxed':'o','LockingBaseline':'s','MultiQueueDequeueMin':'D'}
plt.rcParams.update({'font.family':'DejaVu Sans','font.size':10,'axes.labelsize':10,'axes.titlesize':12,
                     'figure.facecolor':'#fcfcfb','axes.facecolor':'#fcfcfb','text.color':'#0b0b0b',
                     'axes.labelcolor':'#0b0b0b','xtick.color':'#52514e','ytick.color':'#52514e',
                     'axes.edgecolor':'#b6b5ae','svg.fonttype':'path','svg.hashsalt':'bifrost-cpq-20261028','savefig.facecolor':'#fcfcfb'})

def style(ax, ylabel, oversub=True):
    ax.spines[['top','right']].set_visible(False)
    ax.grid(axis='y',color='#deddd8',linewidth=.6,zorder=0)
    ax.set_ylabel(ylabel)
    ax.set_xscale('log',base=2)
    ax.set_xticks(threads if oversub else [1,2,4,8], [str(t) for t in (threads if oversub else [1,2,4,8])])
    ax.set_xlabel('Threads' if oversub else 'Stickiness')
    ax.set_ylim(bottom=0)
    if oversub:
        ax.axvspan(64,128,color='#e7e6e1',alpha=.55,zorder=0)

def band(ax,x,values,color,label,marker='o',linestyle='-'):
    means=[statistics.mean(v) for v in values]
    ax.errorbar(x,means,yerr=[[m-min(v) for m,v in zip(means,values)],[max(v)-m for m,v in zip(means,values)]],
                label=label,color=color,marker=marker,linestyle=linestyle,markersize=6,markeredgecolor='#fcfcfb',markeredgewidth=1.5,linewidth=1.5,capsize=3,elinewidth=.8,zorder=3)

def save(fig,name,title,desc):
    fig.set_layout_engine(None)
    if len(fig.axes)==4:
        fig.subplots_adjust(left=.09,right=.98,bottom=.13,top=.87,hspace=.42,wspace=.25)
    elif len(fig.axes)==2:
        fig.subplots_adjust(left=.08,right=.98,bottom=.16,top=.84,wspace=.24)
    else:
        fig.subplots_adjust(left=.10,right=.98,bottom=.18,top=.84)
    if len(fig.axes)>1:
        for ax in fig.axes:
            panel=ax.get_title(loc='left')
            ax.set_title('',loc='left')
            box=ax.get_position()
            fig.text(box.x0,box.y1+.012,panel,ha='left',va='bottom',fontsize=12,zorder=100)
    fig.savefig(out/f'{name}.svg',metadata={'Date':None})
    tree=ET.parse(out/f'{name}.svg')
    svg=tree.getroot()
    ns='{http://www.w3.org/2000/svg}'
    svg.set('role','img'); svg.set('aria-labelledby',name+'-title '+name+'-desc')
    t=ET.Element(ns+'title',{'id':name+'-title'}); t.text=title
    d=ET.Element(ns+'desc',{'id':name+'-desc'}); d.text=desc
    svg.insert(0,t); svg.insert(1,d)
    tree.write(out/f'{name}.svg',encoding='unicode',xml_declaration=True)
    path=out/f'{name}.svg'
    path.write_text('\n'.join(line.rstrip() for line in path.read_text().splitlines())+'\n')
    plt.close(fig)

fig,axes=plt.subplots(2,2,figsize=(12,8),layout='constrained')
for ax,workload,title in zip(axes.flat,workloads,workload_names):
    for target in palette:
        values=[server[('scaling',target,workload,t,1,0)] for t in threads]
        band(ax,threads,values,palette[target],target_names[target],markers[target])
    style(ax,'Million operations / second')
    ax.set_title(title,loc='left')
axes[0,0].legend(frameon=False,fontsize=8)
fig.suptitle('Xeon 8573C: current queue scaling',fontsize=18,x=.03,ha='left')
fig.supxlabel('Four sweeps; min–max error bars. Mixed workloads count dequeue attempts. Shading: threads above 64.',fontsize=9)
save(fig,'scaling','Current queue scaling on Xeon 8573C','Four workload panels compare relaxed MultiQueue, synchronized BCL, and best-observed scanning with four runs per point.')

fig,axes=plt.subplots(2,2,figsize=(12,8),layout='constrained')
for ax,workload,title in zip(axes.flat,workloads,workload_names):
    for buffer,color,marker in [(0,'#2a78d6','o'),(16,'#eb6834','s')]:
        values=[server[('buffered','MultiQueueRelaxed',workload,t,1,buffer)] for t in threads]
        band(ax,threads,values,color,f'Buffer {buffer}',marker)
    style(ax,'Million operations / second'); ax.set_title(title,loc='left')
axes[0,0].legend(frameon=False)
fig.suptitle('Buffering under contention',fontsize=18,x=.03,ha='left')
fig.supxlabel('Current implementation, stickiness 1. Four runs per point; min–max error bars. Shading: threads above 64.',fontsize=9)
save(fig,'buffering','Buffering under contention','Four workload panels compare buffer capacities zero and sixteen on the current implementation.')

fig,axes=plt.subplots(2,2,figsize=(12,8),layout='constrained')
for ax,workload,title in zip(axes.flat,workloads,workload_names):
    for t,marker,linestyle in [(1,'o','-'),(2,'s','--')]:
        values=[server[('stickiness','MultiQueueRelaxed',workload,t,s,0)] for s in [1,2,4,8]]
        band(ax,[1,2,4,8],values,'#2a78d6',f'Relaxed {t}T',marker,linestyle)
    for t,linestyle in [(1,'-'),(2,'--')]:
        baseline=server[('scaling','LockingBaseline',workload,t,1,0)]
        ax.axhline(statistics.mean(baseline),color='#eb6834',linewidth=.9,linestyle=linestyle,label=f'BCL {t}T')
    style(ax,'Million operations / second',False); ax.set_title(title,loc='left')
axes[0,0].legend(frameon=False)
fig.suptitle('Stickiness in the low-thread regime',fontsize=18,x=.03,ha='left')
fig.supxlabel('Current unbuffered implementation. Four runs per point; min–max error bars. Orange horizontal lines: mean BCL baselines. Dashes identify two threads.',fontsize=9)
save(fig,'stickiness','Stickiness in the low-thread regime','Four workload panels show stickiness levels one, two, four, and eight at one and two threads.')

fig,axes=plt.subplots(2,2,figsize=(12,8),layout='constrained')
for ax,workload,title in zip(axes.flat,workloads,workload_names):
    points=[next(r for r in speedups if r['workload']==workload and r['threads']==t) for t in threads]
    means=[r['mean'] for r in points]
    ax.errorbar(threads,means,yerr=[[r['mean']-r['min'] for r in points],[r['max']-r['mean'] for r in points]],
                color='#2a78d6',marker='o',markersize=6,linewidth=1.5,capsize=3,elinewidth=.8)
    style(ax,'Relaxed / synchronized BCL')
    ax.set_ylim(bottom=min(.5,min(r['min'] for r in points)*.8))
    ax.set_yscale('log',base=2)
    lower,upper=ax.get_ylim()
    ticks=[v for v in [.125,.25,.5,1,2,4,8,16,32,64,128,256] if lower<=v<=upper]
    ax.set_yticks(ticks,[f'{v:g}×' for v in ticks])
    ax.axhline(1,color='#52514e',linewidth=.9,linestyle='--')
    ax.set_title(title,loc='left')
fig.suptitle('Speedup over the synchronized BCL baseline',fontsize=18,x=.03,ha='left')
fig.supxlabel('Paired ratios, n=4, min–max spread. Dashed line = parity; shading = threads above 64.',fontsize=9)
save(fig,'speedup','Speedup over synchronized BCL','Four panels show same-run relaxed-to-locked speedup ratios, with parity marked at one.')

doubling=[]
for t in threads[1:]:
    prior=server[('scaling','MultiQueueRelaxed','UniformMixed5050',t//2,1,0)]
    current=server[('scaling','MultiQueueRelaxed','UniformMixed5050',t,1,0)]
    doubling.append({'from_threads':t//2,'to_threads':t,**stats([a/b for a,b in zip(current,prior)])})
write_csv('doubling-summary.csv',doubling)
fig,ax=plt.subplots(figsize=(10,5.6),layout='constrained')
x=list(range(len(doubling)))
ax.bar(x,[r['mean'] for r in doubling],color='#2a78d6',width=.5,zorder=3)
ax.errorbar(x,[r['mean'] for r in doubling],yerr=[[r['mean']-r['min'] for r in doubling],[r['max']-r['mean'] for r in doubling]],
            fmt='none',ecolor='#0b0b0b',elinewidth=.8,capsize=3,zorder=4)
ax.axhline(2,color='#52514e',linestyle='--',linewidth=.9,label='Ideal doubling')
ax.set_xticks(x,[f"{r['from_threads']} → {r['to_threads']}" for r in doubling])
ax.set_xlabel('Thread-count increase'); ax.set_ylabel('Throughput multiplier')
ax.set_ylim(0,max(2.2,max(r['max'] for r in doubling)*1.15))
ax.spines[['top','right']].set_visible(False); ax.grid(axis='y',color='#deddd8',linewidth=.6)
ax.set_title('Per-doubling scaling: uniform mixed workload',loc='left',pad=18)
ax.legend(frameon=False)
fig.supxlabel('Current relaxed queue. Four paired run ratios per step; min–max error bars. The 64 → 128 step oversubscribes.',fontsize=9)
save(fig,'doubling','Per-doubling scaling','Bars show the observed throughput multiplier for each doubling of thread count in the uniform mixed workload.')

fig,ax=plt.subplots(figsize=(10,5.6),layout='constrained')
for method,color,marker,label in [('MultiQueue_EnqueueDequeue_Int','#2a78d6','o','Relaxed MultiQueue'),('Locking_EnqueueDequeue_Int','#eb6834','s','Synchronized BCL')]:
    points=sorted([r for r in latency_summary if r['kind']=='latency' and r['method']==method],key=lambda r:r['population'])
    band(ax,[r['population'] for r in points],[[r['min'],r['max']] for r in points],color,label,marker)
ax.set_xscale('log'); ax.set_xticks([10,1000,100000,1000000],['10','1k','100k','1M']); ax.set_xlabel('Fixed queue population'); ax.set_ylabel('Nanoseconds / enqueue–dequeue pair')
ax.spines[['top','right']].set_visible(False); ax.grid(axis='y',color='#deddd8',linewidth=.6)
ax.legend(frameon=False); ax.set_title('Value-element pair latency, including sparse population 10',loc='left',pad=18)
fig.supxlabel('Two independent ShortRun jobs. Error bars span the two reported means; they are not confidence intervals.',fontsize=9)
save(fig,'pair-latency','Value-element pair latency','Fixed-population int-element pair latency compares relaxed MultiQueue with the synchronized BCL baseline across two independent ShortRun jobs.')

fig,axes=plt.subplots(1,2,figsize=(12,5.6),layout='constrained')
for ax,method,title in zip(axes,['Buffered_EnqueueDequeue_ValueElement','Buffered_EnqueueDequeue_ReferenceElement'],['Value element: int / int','Reference element: string / long']):
    for buffer,color,marker in [(0,'#2a78d6','o'),(16,'#eb6834','s')]:
        points=sorted([r for r in latency_summary if r['kind']=='buffered-latency' and r['method']==method and r['buffer']==buffer],key=lambda r:r['population'])
        band(ax,[r['population'] for r in points],[[r['min'],r['max']] for r in points],color,f'Buffer {buffer}',marker)
    ax.set_xscale('log'); ax.set_xticks([10,1000,100000,1000000],['10','1k','100k','1M']); ax.set_xlabel('Fixed queue population'); ax.set_ylabel('Nanoseconds / pair')
    ax.set_ylim(bottom=0); ax.spines[['top','right']].set_visible(False)
    ax.grid(axis='y',color='#deddd8',linewidth=.6); ax.set_title(title,loc='left')
axes[0].legend(frameon=False)
fig.suptitle('Buffering and fixed-population pair latency',fontsize=18,x=.03,ha='left')
fig.supxlabel('Two independent ShortRun jobs per case. Error bars span reported means, not confidence intervals.',fontsize=9)
save(fig,'buffered-latency','Buffering and pair latency','Separate value and reference element panels compare zero and sixteen-entry buffers across all four populations.')

cpu={r['field'].rstrip(':'):r['data'] for r in json.loads((root/'lscpu.json').read_text())['lscpu']}
os_name=next(line.split('=',1)[1].strip(chr(34)) for line in (root/'os-release.txt').read_text().splitlines() if line.startswith('PRETTY_NAME='))
endpoint=[r for r in server_summary if r['kind']=='scaling' and r['target']=='MultiQueueRelaxed' and r['threads']==64]
endpoint_ratios=[r['mean'] for r in speedups if r['threads']==64]
abstract=f"At 64 threads, the relaxed queue averages {min(r['mean'] for r in endpoint):.1f}–{max(r['mean'] for r in endpoint):.1f} Mops/s across the four workloads, {min(endpoint_ratios):.1f}–{max(endpoint_ratios):.1f}× the synchronized BCL baseline on this host."
headline=[]
for workload in workloads:
    row=next(r for r in server_summary if r['kind']=='scaling' and r['target']=='MultiQueueRelaxed' and r['workload']==workload and r['threads']==64)
    ratio=next(r for r in speedups if r['workload']==workload and r['threads']==64)
    headline.append(f"| {workload} | {row['mean']:.2f} ({row['min']:.2f}–{row['max']:.2f}) | {ratio['mean']:.2f}× |")

def select(family,version,mode,metric,**where):
    matches=[r for r in validation_summary if r['family']==family and r['version']==version and r['mode']==mode and r['metric']==metric and all(r[k]==v for k,v in where.items())]
    assert len(matches)==1,(family,version,mode,metric,where,len(matches))
    return matches[0]

comparison=[]
cases=[('Dense, 4-thread throughput','throughput','Mops',{'population':4096,'buffer':0,'threads':4},'Mops/s'),
       ('Sparse buffered reference pair','batch-sparse','pairMedianNs',{'population':16,'buffer':16,'payload':'reference','custom':'True'},'ns/pair'),
       ('Dense unbuffered reference stripe proxy','batch','stripeMedianNs',{'population':4096,'buffer':0,'payload':'reference','custom':'True'},'ns/pair'),
       ('Independent reference stripe','stripe','medianNs',{'payload':'reference'},'ns/pair')]
for title,mode,metric,where,unit in cases:
    a=select('original-comparison','original',mode,metric,**where)
    b=select('original-comparison','current',mode,metric,**where)
    comparison.append(f"| {title} | {a['mean']:.2f} | {b['mean']:.2f} | {(b['mean']/a['mean']-1)*100:+.2f}% | {unit} |")

optimization=[]
opt_cases=[
    ('Inlining: dense 4-thread throughput','old','throughput','Mops',{'population':4096,'buffer':0,'threads':4},'Mops/s'),
    ('Copy/guard: sparse buffered throughput','preprofile','buffered-single','Mops',{'population':16,'buffer':16,'threads':1},'Mops/s'),
    ('Copy/guard: sparse buffered reference pair','preprofile','batch-sparse','pairMedianNs',{'population':16,'buffer':16,'payload':'reference','custom':'True'},'ns/pair'),
    ('Inlining: dense reference stripe proxy','old','batch','stripeMedianNs',{'population':4096,'buffer':0,'payload':'reference','custom':'True'},'ns/pair'),
    ('Inlining: independent reference stripe','old','stripe','medianNs',{'payload':'reference'},'ns/pair'),
]
for title,version,mode,metric,where,unit in opt_cases:
    a=select('optimization-comparison',version,mode,metric,**where)
    b=select('optimization-comparison','current',mode,metric,**where)
    optimization.append(f"| {title} | {a['mean']:.2f} | {b['mean']:.2f} | {(b['mean']/a['mean']-1)*100:+.2f}% | {unit} |")

allocation_max=max(r['max_allocated_bytes'] for r in latency_summary)
summary={'cpu':cpu,'source_commit':(root/'commit.txt').read_text().strip(),
         'server_groups':len(server_summary),'latency_groups':len(latency_summary),
         'validation_groups':len(validation_summary),'max_bdn_allocated_bytes':allocation_max,
         'server':server_summary,'latency':latency_summary,'validation':validation_summary,'speedups':speedups}
(out/'summary.json').write_text(json.dumps(summary,indent=2))
report=f'''# Xeon 8573C benchmark campaign — October 2026

This campaign measures the comparer-safe priority API implementation in Bifrost v0.6.0 with the toolkit committed in PR #74.

{abstract}

## Environment and protocol

- Source/harness commit: `{summary['source_commit']}`.
- Host: Azure Standard_D64s_v6, eastus2 zone 1; {cpu['Model name']}; 64 logical / 32 physical cores, one NUMA node.
- OS: {os_name}. The June report used Ubuntu 24.04.4 LTS.
- Runtime: .NET 10.0.9; SDK 10.0.301; Workstation GC. The raw archive records OS image, package versions, affinity, CPU policy and environment.
- Four independent three-second server sweeps per variant, including 128-thread oversubscription. Drain rates use actual elapsed drain time.
- Two independent BenchmarkDotNet ShortRun jobs per latency/allocation class. These timings are per enqueue/dequeue pair.
- Original/current ABBA and pre-inlining/pre-copy/current/current/pre-copy/pre-inlining order, three blocks each. Each control has six process observations per comparison family.
- All verification and control builds preceded timing. No performance processes ran concurrently.

## Current scaling at 64 threads

| Workload | Relaxed Mops/s: mean (min–max), n=4 | Speedup over synchronized BCL |
|---|---:|---:|
'''+ '\n'.join(headline)+'''

![Current scaling](scaling.svg)

Mixed and split workload rates include dequeue attempts, including unsuccessful attempts. They are not all successful-delivery rates.
`MultiQueueDequeueMin` in raw CSV is a legacy label for the best-observed scan, not an exact concurrent minimum.
The synchronized BCL baseline replaces the removed standalone locking queue; June baseline descriptions do not describe this implementation.

## Buffering and stickiness

![Buffering](buffering.svg)

![Stickiness](stickiness.svg)

![Speedup over synchronized BCL](speedup.svg)

![Per-doubling scaling](doubling.svg)

## Latency and allocation

![Pair latency](pair-latency.svg)

![Buffering and pair latency](buffered-latency.svg)

Maximum allocation across all reported BenchmarkDotNet pair rows: '''+f'{allocation_max:g} B per benchmark invocation (one enqueue/dequeue pair)'+'''. See [the complete latency table](latency-summary.csv) for every method, population and buffer setting.
The unsynchronized BCL control is a single-thread reference only; it is not the thread-safe baseline.

## Implementation tradeoffs on this host

| Metric | Original | Current | Current change | Unit |
|---|---:|---:|---:|---|
'''+ '\n'.join(comparison)+'''

Throughput increases are faster; latency increases are slower. Source-direct Mops/s counts enqueue and dequeue operations separately.
The source-direct queue uses 32 stripes and populations 16/4096; the server sweep uses the 256-stripe default. Original control `6800ae0` lacks the current arbitrary-comparer exception guarantee.
Source-direct reference payloads use object/int; buffered BDN reference rows use string/long.
Stripe proxies exclude whole-queue routing. Their results do not replace full-matrix measurements or measure production lock-hold duration.
The complete [validation table](validation-summary.csv) records every payload/comparer/population/buffer case, batch p90 values, and allocation counters.
Batch p90 is the percentile of batch averages, not individual-operation tail latency. Min–max is spread across process observations, not a confidence interval.

### October optimization controls

These comparisons isolate the inlining and copy/guard changes using the committed reverse patches and verified source hashes.
`old` identifies the pre-inlining control; `preprofile` identifies the pre-copy/guard control. Each row compares six process observations per revision.

| Metric and change | Before optimization | Current | Current change | Unit |
|---|---:|---:|---:|---|
'''+ '\n'.join(optimization)+'''

These are same-host comparisons, separate from the original-versus-current family above. Different compilation and workload contexts can produce different stripe results.

## Verification and limits

The preparation logs record regular and stress tests, both Native AOT samples, 72 trace hashes, and 160 seeded rank/delay records.
Original and current trace/quality outputs match. Reconstructed optimization controls also match those outputs and their committed source hashes.
Sequential trace equivalence does not establish every concurrent interleaving or a universal rank bound.
The reported spread is descriptive. Two ShortRun jobs or six process medians do not establish a precise one-percent change.
VM host contention, OS patch differences and workload shape limit comparisons with June measurements. Compare this campaign's revisions on this host.
These queue-level benchmarks do not measure complete orchestrator or handler execution latency.

## Data and reproduction

- [Server table](server-summary.csv), [paired speedup table](speedup-summary.csv), [latency table](latency-summary.csv), [control table](validation-summary.csv), and [machine-readable summary](summary.json).
- The raw archive retains each repeat separately, complete stdout/stderr, BenchmarkDotNet artifacts, control sources/binaries, source hashes, verification logs and process order.
- The compact [raw dataset](raw/) retains every numeric input, validation stdout, compressed BDN logs, verification logs, provenance and process order. [Collection protocol and archive checksum](raw/protocol.json).
- Install `analysis-requirements.txt`, then run `python analyze_campaign.py raw OUTPUT_DIRECTORY` from this directory to reproduce the tables and figures. The complete extracted archive is also a supported input.
- [Campaign runbook](../../2026-10-priority-api-benchmark-runbook.md) describes the protocol and units.
'''
(out/'README.md').write_text(report)
print(f'Aggregated {len(server_summary)} server, {len(latency_summary)} latency, {len(validation_summary)} control groups into {out}')
