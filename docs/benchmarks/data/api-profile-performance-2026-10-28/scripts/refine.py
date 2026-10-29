from pathlib import Path
import shutil,subprocess
r=Path('/private/tmp/bifrost-profile');base=(r/'before/SubQueue.cs').read_text()
pop='''        if (remaining > 0)
        {
            d.Slice(1, remaining).CopyTo(d.Slice(0, remaining));
        }'''
for name in ['poploop','guardfirst','poploop-guard']:
 source=r/f'{name}-sources';shutil.copytree(r/'before',source,dirs_exist_ok=True);s=base
 if name.startswith('poploop'):
  s=s.replace(pop,'''        if (!RuntimeHelpers.IsReferenceOrContainsReferences<(TElement, TPriority)>())
        {
            for (int i = 0; i < remaining; i++) d[i] = d[i + 1];
        }
        else if (remaining > 0)
        {
            d.Slice(1, remaining).CopyTo(d.Slice(0, remaining));
        }''')
 if 'guard' in name:
  s=s.replace('_header.Count > 2 && _deletionCount == 1 && !HasNonThrowingComparer','!HasNonThrowingComparer && _header.Count > 2 && _deletionCount == 1').replace('_insertionCount == _bufferCapacity && !HasNonThrowingComparer','!HasNonThrowingComparer && _insertionCount == _bufferCapacity')
 (source/'SubQueue.cs').write_text(s)
 with (r/f'build-validation-{name}.txt').open('w') as f:subprocess.run(['dotnet','build','tools/queue-validation/Validation.csproj','-c','Release',f'-p:QueueSources={source}','-o',str(r/f'validation-{name}')],stdout=f,stderr=subprocess.STDOUT,check=True)
