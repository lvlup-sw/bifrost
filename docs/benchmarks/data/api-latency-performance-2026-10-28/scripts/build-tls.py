from pathlib import Path
import shutil,subprocess
import sys
r=Path(sys.argv[1]) if len(sys.argv)>1 else Path('/private/tmp/bifrost-latency');base=(r/'before/ThreadHandle.cs').read_text()
old='    internal static ThreadHandle Current => t_current ??= new ThreadHandle();'
for v in ['tls-inline','tls-split','tls-pop']:
 src=r/f'{v}-sources';shutil.copytree(r/('plain-inline-sources' if v=='tls-pop' else 'before'),src,dirs_exist_ok=True)
 new='''    internal static ThreadHandle Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => t_current ??= new ThreadHandle();
    }'''
 if v!='tls-inline':new='''    internal static ThreadHandle Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => t_current ?? CreateCurrent();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ThreadHandle CreateCurrent() => t_current = new ThreadHandle();'''
 assert old in base;(src/'ThreadHandle.cs').write_text(base.replace(old,new))
 with (r/f'build-{v}.txt').open('w') as f:subprocess.run(['dotnet','build','tools/queue-validation/Validation.csproj','-c','Release',f'-p:QueueSources={src}','-o',str(r/v)],stdout=f,stderr=subprocess.STDOUT,check=True)
