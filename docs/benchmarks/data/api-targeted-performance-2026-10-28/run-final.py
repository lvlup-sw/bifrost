from pathlib import Path
import os, subprocess, sys
r = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).parent
order = ['original', 'prior', 'final', 'final', 'prior', 'original']
for mode, blocks in [('perf', 1), ('batch', 1), ('batch-sparse', 3), ('dense', 3)]:
    env = dict(os.environ)
    if mode.startswith('batch'):
        env['DOTNET_TieredCompilation'] = '0'
    else:
        env.pop('DOTNET_TieredCompilation', None)
    for block in range(blocks):
        for slot, name in enumerate(order):
            args = ['dotnet', str(r / name / 'Validation.dll')]
            if mode != 'perf':
                args.append(mode)
            file = r / f'final-{mode}-{block}-{slot}-{name}.txt'
            with file.open('w') as output:
                subprocess.run(args, stdout=output, stderr=subprocess.STDOUT, env=env, check=True)
            print(file.name, flush=True)
