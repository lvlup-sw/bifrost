from pathlib import Path
import os,shutil,subprocess
r=Path('/private/tmp/bifrost-residual')
for name,source in [('k','j-sources'),('l','before')]:
 folder=r/(name+'-sources');shutil.copytree(r/source,folder,dirs_exist_ok=True)
 p=folder/'SubQueue.cs';p.write_text(p.read_text().replace('    private void SortedInsertIntoDeletion(', '    [MethodImpl(MethodImplOptions.AggressiveInlining)]\n    private void SortedInsertIntoDeletion('))
 with (r/f'build-{name}.txt').open('w') as f:subprocess.run(['dotnet','build','tools/queue-validation/Validation.csproj','-c','Release',f'-p:QueueSources={folder}','-o',str(r/name)],stdout=f,stderr=subprocess.STDOUT,check=True)
for block in range(2):
 for slot,name in enumerate(['prior','k','l','l','k','prior']):
  with (r/f'check-kl-{block}-{slot}-{name}.txt').open('w') as f:subprocess.run(['dotnet',str(r/name/'Validation.dll'),'batch-sparse'],stdout=f,stderr=subprocess.STDOUT,env={**os.environ,'DOTNET_TieredCompilation':'0'},check=True)
  print(block,slot,name,flush=True)
