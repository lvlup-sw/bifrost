from pathlib import Path
import shutil,subprocess
r=Path('/private/tmp/bifrost-profile');base=(r/'before/SubQueue.cs').read_text()
pop='''        if (remaining > 0)
        {
            d.Slice(1, remaining).CopyTo(d.Slice(0, remaining));
        }'''
insert='''        int shift = _deletionCount - hole;
        if (shift > 0)
        {
            d.Slice(hole, shift).CopyTo(d.Slice(hole + 1, shift));
        }'''
for name in ['loops','one','current','earlier','original']:
 source={'current':r/'before','earlier':Path('/private/tmp/bifrost-targeted/before'),'original':Path('/private/tmp/bifrost-validation/baseline')}.get(name,r/f'{name}-sources')
 if name in ['loops','one']:
  shutil.copytree(r/'before',source,dirs_exist_ok=True)
  if name=='loops':
   s=base.replace(pop,'''        for (int i = 0; i < remaining; i++) d[i] = d[i + 1];''').replace(insert,'''        for (int i = _deletionCount; i > hole; i--) d[i] = d[i - 1];''')
  else:
   s=base.replace(pop,'''        if (remaining == 1) d[0] = d[1];
        else if (remaining > 1)
        {
            d.Slice(1, remaining).CopyTo(d.Slice(0, remaining));
        }''').replace(insert,'''        int shift = _deletionCount - hole;
        if (shift == 1) d[hole + 1] = d[hole];
        else if (shift > 1)
        {
            d.Slice(hole, shift).CopyTo(d.Slice(hole + 1, shift));
        }''')
  (source/'SubQueue.cs').write_text(s)
 with (r/f'build-validation-{name}.txt').open('w') as f:subprocess.run(['dotnet','build','tools/queue-validation/Validation.csproj','-c','Release',f'-p:QueueSources={source}','-o',str(r/f'validation-{name}')],stdout=f,stderr=subprocess.STDOUT,check=True)
