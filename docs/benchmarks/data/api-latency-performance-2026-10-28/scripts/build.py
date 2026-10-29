from pathlib import Path
import shutil,subprocess
import sys
r=Path(sys.argv[1]) if len(sys.argv)>1 else Path('/private/tmp/bifrost-latency');base=(r/'before/SubQueue.cs').read_text()
old='!HasNonThrowingComparer && _header.Count > 2 && _deletionCount == 1'
changes={'all': '!HasNonThrowingComparer & (_header.Count > 2) & (_deletionCount == 1)', 'partial':'!HasNonThrowingComparer && ((_header.Count > 2) & (_deletionCount == 1))'}
for v in ['current','original','preprofile','all','partial','both','inline']:
 src={'current':r/'before','original':Path('/private/tmp/bifrost-validation/baseline'),'preprofile':Path('/private/tmp/bifrost-profile/before')}.get(v,r/f'{v}-sources')
 if v in ['all','partial','both','inline']:
  shutil.copytree(r/'before',src,dirs_exist_ok=True)
  s=base.replace(old,changes['all' if v in ['all','both'] else 'partial'])
  if v=='both':s=s.replace('!HasNonThrowingComparer && _insertionCount == _bufferCapacity','!HasNonThrowingComparer & (_insertionCount == _bufferCapacity)')
  if v=='inline':s=s.replace('    internal SubQueuePopStatus PopHeldRoot(', '    [MethodImpl(MethodImplOptions.AggressiveInlining)]\n    internal SubQueuePopStatus PopHeldRoot(')
  (src/'SubQueue.cs').write_text(s)
 with (r/f'build-{v}.txt').open('w') as f:subprocess.run(['dotnet','build','tools/queue-validation/Validation.csproj','-c','Release',f'-p:QueueSources={src}','-o',str(r/v)],stdout=f,stderr=subprocess.STDOUT,check=True)
