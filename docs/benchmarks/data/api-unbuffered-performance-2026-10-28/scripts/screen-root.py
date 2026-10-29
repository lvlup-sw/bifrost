from pathlib import Path
import subprocess,os
r=Path('/private/tmp/bifrost-unbuffered');d=r/'screen-root';d.mkdir(exist_ok=True)
vs=['current','root','root-local']
for i,v in enumerate(vs+vs[::-1]):
 dll={'old':Path('/private/tmp/bifrost-latency/current/Validation.dll'),'tls-only':Path('/private/tmp/bifrost-latency/tls-split/Validation.dll')}.get(v,r/v/'Validation.dll')
 with (d/f'{i:02}-{v}.txt').open('w') as f:subprocess.run(['dotnet',str(dll),'batch'],env={**os.environ,'DOTNET_TieredCompilation':'0'},stdout=f,stderr=subprocess.STDOUT,check=True)
 print(i,v,flush=True)
