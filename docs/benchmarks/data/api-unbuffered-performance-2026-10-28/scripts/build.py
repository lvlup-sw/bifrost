from pathlib import Path
import subprocess,shutil
r=Path('/private/tmp/bifrost-unbuffered');base=(r/'before/SubQueue.cs').read_text()
variants={
 'current':base,
 'unbuffered-inline':base.replace('    private SubQueuePopStatus UnbufferedPopHeldRoot(', '    [MethodImpl(MethodImplOptions.AggressiveInlining)]\n    private SubQueuePopStatus UnbufferedPopHeldRoot('),
 'unbuffered-noinline':base.replace('    private SubQueuePopStatus UnbufferedPopHeldRoot(', '    [MethodImpl(MethodImplOptions.NoInlining)]\n    private SubQueuePopStatus UnbufferedPopHeldRoot('),
 'direct':base.replace('            return PopHeldRoot(out element, out priority);','            if (_bufferCapacity == 0) return UnbufferedPopHeldRoot(out element, out priority);\n            return PopHeldRoot(out element, out priority);'),
 'cold':base.replace('return BufferedTransaction(default!, default!, pop: true, out element, out priority);','return BufferedPopTransaction(out element, out priority);').replace('    private SubQueuePopStatus UnbufferedPopHeldRoot(', '    [MethodImpl(MethodImplOptions.NoInlining)]\n    private SubQueuePopStatus BufferedPopTransaction(out TElement element, out TPriority priority)\n        => BufferedTransaction(default!, default!, pop: true, out element, out priority);\n\n    private SubQueuePopStatus UnbufferedPopHeldRoot('),
}
for v,s in variants.items():
 src=r/f'{v}-sources';shutil.copytree(r/'before',src,dirs_exist_ok=True);(src/'SubQueue.cs').write_text(s)
 with (r/f'build-{v}.txt').open('w') as f:subprocess.run(['dotnet','build','tools/queue-validation/Validation.csproj','-c','Release',f'-p:QueueSources={src}','-o',str(r/v)],stdout=f,stderr=subprocess.STDOUT,check=True)
 print('built',v,flush=True)
