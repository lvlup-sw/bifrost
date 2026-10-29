exec(open('/private/tmp/bifrost-unbuffered/build.py').read().split('variants=')[0])
variants={}
for v,local in [('root',False),('root-local',True)]:
 s=base
 for kind in ['Default','Custom']:
  s=s.replace(f'                MoveDown{kind}Comparer(0, moving.Element',f'                MoveDown{kind}Comparer(moving.Element')
  a=s.index(f'    private void MoveDown{kind}Comparer('); b=s.index('\n    /// <summary>',a)
  section=s[a:b].replace('(int index, TElement element','(TElement element').replace('        int start = index;','        int index = 0;').replace('nodes[start].Priority','nodes[0].Priority').replace('        index = start;','        index = 0;')
  if local and kind=='Custom':section=section.replace('        var nodes = _nodes;','        var nodes = _nodes;\n        var comparer = _comparer!;').replace('_comparer!.Compare(','comparer.Compare(')
  s=s[:a]+section+s[b:]
 variants[v]=s
for v,s in variants.items():
 src=r/f'{v}-sources';shutil.copytree(r/'before',src,dirs_exist_ok=True);(src/'SubQueue.cs').write_text(s)
 with (r/f'build-{v}.txt').open('w') as f:subprocess.run(['dotnet','build','tools/queue-validation/Validation.csproj','-c','Release',f'-p:QueueSources={src}','-o',str(r/v)],stdout=f,stderr=subprocess.STDOUT,check=True)
 print('built',v,flush=True)
