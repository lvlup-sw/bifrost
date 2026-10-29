exec(open('/private/tmp/bifrost-unbuffered/build.py').read().split('variants=')[0])
a=base.index('    private void MoveDownCustomComparer(');b=base.index('    /// <summary>Commits a compared ascent',a)
s=base[:a]+base[a:b].replace('        var nodes = _nodes;','        var nodes = _nodes;\n        var comparer = _comparer!;').replace('_comparer!.Compare(', 'comparer.Compare(')+base[b:]
variants={'comparer-local':s,'local-inline':s.replace('    private SubQueuePopStatus UnbufferedPopHeldRoot(', '    [MethodImpl(MethodImplOptions.AggressiveInlining)]\n    private SubQueuePopStatus UnbufferedPopHeldRoot(')}
for v,s in variants.items():
 src=r/f'{v}-sources';shutil.copytree(r/'before',src,dirs_exist_ok=True);(src/'SubQueue.cs').write_text(s)
 with (r/f'build-{v}.txt').open('w') as f:subprocess.run(['dotnet','build','tools/queue-validation/Validation.csproj','-c','Release',f'-p:QueueSources={src}','-o',str(r/v)],stdout=f,stderr=subprocess.STDOUT,check=True)
 print('built',v,flush=True)
