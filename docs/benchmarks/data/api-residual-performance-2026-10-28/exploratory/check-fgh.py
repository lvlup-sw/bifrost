from pathlib import Path
import shutil,subprocess,os
r=Path('/private/tmp/bifrost-residual');base=(r/'before/SubQueue.cs').read_text()
start=base.index('        // With at most one remaining entry,')
end=base.index('        return BufferedPopHeldRoot(out element, out priority);',start)
guard=base[start:end].replace('if (_header.Count > 2 && _deletionCount == 1 && !HasNonThrowingComparer)', 'if (_header.Count > 2 && _deletionCount == 1 && !HasNonThrowingComparer && !_journalActive)')
for name in ['f','g','h']:
 folder=r/(name+'-sources');shutil.copytree(r/'before',folder,dirs_exist_ok=True)
 s=base[:start]+base[end:]
 s=s.replace('    private SubQueuePopStatus BufferedPopHeldRoot(out TElement element, out TPriority priority)\n    {','    private SubQueuePopStatus BufferedPopHeldRoot(out TElement element, out TPriority priority)\n    {\n'+guard)
 if name in ['g','h']:
  a=s.index('                if (_insertionCount == _bufferCapacity && !HasNonThrowingComparer)')
  b=s.index('\n            }\n            else',a)
  s=s[:a]+'                BufferedPush(element, priority);'+s[b:]
  s=s.replace('    private void BufferedPush(TElement element, TPriority priority)\n    {', '''    private void BufferedPush(TElement element, TPriority priority)
    {
        if (_insertionCount == _bufferCapacity && !HasNonThrowingComparer && !_journalActive)
        {
            BufferedTransaction(element, priority, pop: false, out _, out _);
            return;
        }
''')
 if name=='h':s=s.replace('    private void UnbufferedPush(', '    [MethodImpl(MethodImplOptions.NoInlining)]\n    private void UnbufferedPush(')
 (folder/'SubQueue.cs').write_text(s)
 with (r/f'build-{name}.txt').open('w') as f:subprocess.run(['dotnet','build','tools/queue-validation/Validation.csproj','-c','Release',f'-p:QueueSources={folder}','-o',str(r/name)],stdout=f,stderr=subprocess.STDOUT,check=True)
for block in range(2):
 for slot,name in enumerate(['prior','f','g','h','h','g','f','prior']):
  with (r/f'check-fgh-{block}-{slot}-{name}.txt').open('w') as f:subprocess.run(['dotnet',str(r/name/'Validation.dll'),'batch-sparse'],env={**os.environ,'DOTNET_TieredCompilation':'0'},stdout=f,stderr=subprocess.STDOUT,check=True)
  print(block,slot,name,flush=True)
