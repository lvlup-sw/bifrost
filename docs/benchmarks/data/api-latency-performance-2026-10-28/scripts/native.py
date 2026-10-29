from pathlib import Path
import os,subprocess,time
import sys
r=Path(sys.argv[1]) if len(sys.argv)>1 else Path('/private/tmp/bifrost-latency');old=Path(sys.argv[2]) if len(sys.argv)>2 else r
for mode in ['batch-value','batch-reference']:
 for v in ['current','final']:
  key=v+'-'+mode
  with (r/f'profile-process-{key}.txt').open('w') as log:
   p=subprocess.Popen(['dotnet',str(r/f'profile-{v}/Profile.dll'),mode,'16'],env={**os.environ,'DOTNET_TieredCompilation':'0'},stdout=log,stderr=subprocess.STDOUT)
   time.sleep(1)
   subprocess.run(['sample',str(p.pid),'8','1','-file',str(r/f'native-{key}.txt')],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
   with (r/f'native-map-{key}.txt').open('w') as f:subprocess.run([str(old/'tools/dotnet-trace'),'collect','--profile','dotnet-common','--process-id',str(p.pid),'--duration','00:00:02','--output',str(r/f'native-map-{key}.nettrace')],stdout=f,stderr=subprocess.STDOUT,check=True)
   assert p.wait()==0,key
  with (r/f'native-map-{key}.json').open('w') as f:subprocess.run(['dotnet',str(old/'reader/bin/Release/net10.0/Reader.dll'),str(r/f'native-map-{key}.nettrace')],stdout=f,check=True)
  print(key,flush=True)
