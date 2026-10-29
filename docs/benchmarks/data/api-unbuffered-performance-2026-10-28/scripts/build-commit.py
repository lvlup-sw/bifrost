exec(open('/private/tmp/bifrost-unbuffered/build.py').read().split('variants=')[0])
a=base.index('    private void MoveDownCustomComparer(');b=base.index('    /// <summary>Commits a compared ascent',a)
custom=base[a:b];start=custom.index('        index = start;');tail=custom[start:];assert tail.endswith('    }\n\n')
commit=tail[:-7].replace('        index = start;','        var nodes = _nodes;')
helper='    /// <summary>Commits a fully compared descent, preserving the transaction journal.</summary>\n    [MethodImpl(MethodImplOptions.NoInlining)]\n    private void CommitDescent(int index, Span<int> path, int length, TElement element, TPriority priority)\n    {\n'+commit+'    }\n\n'
variants={}
for v,kinds in [('commit-custom',['Custom']),('commit-both',['Default','Custom'])]:
 s=base
 for kind in kinds:
  a=s.index(f'    private void MoveDown{kind}Comparer(');b=s.index('\n    /// <summary>',a);x=s[a:b];c=x.index('        index = start;');s=s[:a]+x[:c]+'        CommitDescent(start, path, length, element, priority);\n    }\n'+s[b:]
 s=s.replace('    /// <summary>Commits a compared ascent',helper+'    /// <summary>Commits a compared ascent');variants[v]=s
for v,s in variants.items():
 src=r/f'{v}-sources';shutil.copytree(r/'before',src,dirs_exist_ok=True);(src/'SubQueue.cs').write_text(s)
 with (r/f'build-{v}.txt').open('w') as f:subprocess.run(['dotnet','build','tools/queue-validation/Validation.csproj','-c','Release',f'-p:QueueSources={src}','-o',str(r/v)],stdout=f,stderr=subprocess.STDOUT,check=True)
 print('built',v,flush=True)
