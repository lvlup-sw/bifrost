from pathlib import Path
import os,subprocess,time,sys
r=Path(sys.argv[1])
cases=[tuple(case.split(':',1)) for case in sys.argv[2:]]
if not cases: raise SystemExit('Specify version:scenario pairs after the benchmark root.')
for version,scenario in cases:
 key=version+'-'+scenario;batch=scenario.startswith('batch');env=dict(os.environ)
 if batch:env['DOTNET_TieredCompilation']='0'
 with (r/f'profile-process-{key}.txt').open('w') as out:
  p=subprocess.Popen(['dotnet',str(r/version/'Profile.dll'),scenario,'16' if batch else '5'],stdout=out,stderr=subprocess.STDOUT,env=env)
  time.sleep(1 if batch else 10 if scenario.endswith('sparse') else 26)
  subprocess.run(['sample',str(p.pid),'8','1','-file',str(r/f'native-{key}.txt')],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
  with (r/f'native-map-{key}.txt').open('w') as f:subprocess.run([str(r/'tools/dotnet-trace'),'collect','--profile','dotnet-common','--process-id',str(p.pid),'--duration','00:00:02','--output',str(r/f'native-map-{key}.nettrace')],stdout=f,stderr=subprocess.STDOUT,check=True)
  p.wait()
  if p.returncode:raise RuntimeError((key,p.returncode))
 with (r/f'native-map-{key}.json').open('w') as f:subprocess.run(['dotnet',str(r/'reader/bin/Release/net10.0/Reader.dll'),str(r/f'native-map-{key}.nettrace')],stdout=f,check=True)
 print(key,'exit',p.returncode,flush=True)
