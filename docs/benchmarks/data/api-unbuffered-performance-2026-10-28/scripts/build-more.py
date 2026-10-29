exec(open('/private/tmp/bifrost-unbuffered/build.py').read().split('variants=')[0])
start=base.index('        // Buffered removal serves D.front()')
end=base.index('\n    /// <summary>',start)
section=base[start:end]
# Preserve identical predicates and comparison/mutation order, change only branch layout.
inv=section.replace('        if (_bufferCapacity == 0) return UnbufferedPopHeldRoot(out element, out priority);','        if (_bufferCapacity != 0)\n        {').replace('        return BufferedPopHeldRoot(out element, out priority);','        return BufferedPopHeldRoot(out element, out priority);\n        }\n\n        return UnbufferedPopHeldRoot(out element, out priority);')
variants={'inverted':base[:start]+inv+base[end:],
 'heap-inline':base.replace('    internal bool TryHeapPop(', '    [MethodImpl(MethodImplOptions.AggressiveInlining)]\n    internal bool TryHeapPop('),
 'result':base.replace('        try\n        {\n            return PopHeldRoot(out element, out priority);\n        }\n        finally\n        {\n            SyncLock.Exit();\n        }', '        SubQueuePopStatus result;\n        try\n        {\n            result = PopHeldRoot(out element, out priority);\n        }\n        finally\n        {\n            SyncLock.Exit();\n        }\n\n        return result;')}
for v,s in variants.items():
 src=r/f'{v}-sources';shutil.copytree(r/'before',src,dirs_exist_ok=True);(src/'SubQueue.cs').write_text(s)
 with (r/f'build-{v}.txt').open('w') as f:subprocess.run(['dotnet','build','tools/queue-validation/Validation.csproj','-c','Release',f'-p:QueueSources={src}','-o',str(r/v)],stdout=f,stderr=subprocess.STDOUT,check=True)
 print('built',v,flush=True)
