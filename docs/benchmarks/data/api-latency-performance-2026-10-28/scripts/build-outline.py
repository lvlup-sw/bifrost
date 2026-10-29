from pathlib import Path
import shutil,subprocess
import sys
r=Path(sys.argv[1]) if len(sys.argv)>1 else Path('/private/tmp/bifrost-latency');base=(r/'before/SubQueue.cs').read_text()
for v in ['outline','outline-inline','outline-partial']:
 src=r/f'{v}-sources';shutil.copytree(r/'before',src,dirs_exist_ok=True);s=base.replace('return BufferedTransaction(default!, default!, pop: true, out element, out priority);','return BufferedPopTransaction(out element, out priority);')
 marker='    /// <summary>Rolls back a compound buffer operation without invoking user code.</summary>'
 helper='''    [MethodImpl(MethodImplOptions.NoInlining)]
    private SubQueuePopStatus BufferedPopTransaction(out TElement element, out TPriority priority)
        => BufferedTransaction(default!, default!, pop: true, out element, out priority);

'''
 s=s.replace(marker,helper+marker)
 if v!='outline':s=s.replace('    internal SubQueuePopStatus PopHeldRoot(', '    [MethodImpl(MethodImplOptions.AggressiveInlining)]\n    internal SubQueuePopStatus PopHeldRoot(')
 if v=='outline-partial':s=s.replace('!HasNonThrowingComparer && _header.Count > 2 && _deletionCount == 1','!HasNonThrowingComparer && ((_header.Count > 2) & (_deletionCount == 1))')
 (src/'SubQueue.cs').write_text(s)
 with (r/f'build-{v}.txt').open('w') as f:subprocess.run(['dotnet','build','tools/queue-validation/Validation.csproj','-c','Release',f'-p:QueueSources={src}','-o',str(r/v)],stdout=f,stderr=subprocess.STDOUT,check=True)
