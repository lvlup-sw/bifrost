from pathlib import Path
import os,subprocess,re
import sys
r=Path(sys.argv[1]) if len(sys.argv)>1 else Path('/private/tmp/bifrost-profile')
for v in ['current','poploop','poploop-guard']:
 env={**os.environ,'DOTNET_TieredCompilation':'0','DOTNET_JitDisasm':'*BufferedPopHeldRoot*','DOTNET_JitStdOutFile':str(r/f'jit-copy-{v}.txt')}
 subprocess.run(['dotnet',str(r/f'validation-{v}/Validation.dll'),'batch-sparse'],env=env,stdout=subprocess.DEVNULL,check=True)
 s=(r/f'jit-copy-{v}.txt').read_text()
 for section in s.split('; Assembly listing for method ')[1:]:
  print(v,section.splitlines()[0],re.search(r'Total bytes of code (\d+)',section)[1])
