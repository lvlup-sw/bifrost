from pathlib import Path
import shutil,subprocess
import sys
r=Path(sys.argv[1]) if len(sys.argv)>1 else Path('/private/tmp/bifrost-latency');base=(r/'before/SubQueue.cs').read_text()
for v in ['plain-inline','pushcache','pushcache-inline']:
 src=r/f'{v}-sources';shutil.copytree(r/'before',src,dirs_exist_ok=True);s=base
 if 'pushcache' in v:
  s=s.replace('    private int _insertionCount;', '    private int _insertionCount;\n    private bool _bufferedPushNeedsTransaction;')
  s=s.replace('if (!HasNonThrowingComparer && _insertionCount == _bufferCapacity)', 'if (_bufferedPushNeedsTransaction)')
  s=s.replace('        _insertionCount++;', '        _insertionCount++;\n        _bufferedPushNeedsTransaction = _insertionCount == _bufferCapacity && !HasNonThrowingComparer;')
  s=s.replace('        _insertionCount = 0;', '        _insertionCount = 0;\n        _bufferedPushNeedsTransaction = false;')
  s=s.replace('            _insertionCount = insertionCount;', '            _insertionCount = insertionCount;\n            _bufferedPushNeedsTransaction = insertionCount == _bufferCapacity && !HasNonThrowingComparer;')
 if 'inline' in v:s=s.replace('    internal SubQueuePopStatus PopHeldRoot(', '    [MethodImpl(MethodImplOptions.AggressiveInlining)]\n    internal SubQueuePopStatus PopHeldRoot(')
 (src/'SubQueue.cs').write_text(s)
 with (r/f'build-{v}.txt').open('w') as f:subprocess.run(['dotnet','build','tools/queue-validation/Validation.csproj','-c','Release',f'-p:QueueSources={src}','-o',str(r/v)],stdout=f,stderr=subprocess.STDOUT,check=True)
