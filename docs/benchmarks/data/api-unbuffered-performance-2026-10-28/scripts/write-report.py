from pathlib import Path
import json
r=Path('/private/tmp/bifrost-unbuffered');d=Path('docs/benchmarks/data/api-unbuffered-performance-2026-10-28');s=json.loads((r/'preservation/summary.json').read_text())
text=(r/'report-intro.md').read_text()+'\n## Preserved latency and buffered throughput\n\nSparse isolated custom-comparer queue, population 16, buffer 16; six processes per version:\n\n| Payload | O, ns/pair | C, ns/pair | C vs O |\n|---|---:|---:|---:|\n'
for payload in ['value','reference']:
 x=s[f'batch-sparse,{payload},True,16,16,,pairMedianNs'];a=x['original']['median'];b=x['current']['median'];text+=f'| {payload} | {a:.3f} | {b:.3f} | {(b/a-1)*100:+.2f}% |\n'
text+='\nThe value gap remains recovered. The reference gap is **0.59%**, consistent with the earlier 0.61%, with overlapping process ranges. No further latency improvement is claimed.\n\nFull throughput matrix, Mops/s (enqueue and dequeue counted separately); four processes per version:\n\n| Population | Buffer | Threads | P | C | C vs P |\n|---:|---:|---:|---:|---:|---:|\n'
for pop in [16,4096]:
 for buf in [0,16]:
  for threads in [1,4]:
   x=s[f'perf,,,{pop},{buf},{threads},Mops'];a=x['preprofile']['median'];b=x['current']['median'];text+=f'| {pop} | {buf} | {threads} | {a:.3f} | {b:.3f} | {(b/a-1)*100:+.2f}% |\n'
text+='''
Buffered single-thread gains over P remain **4.29% sparse / 7.56% dense**. Buffered four-thread medians are level or higher. The sparse unbuffered four-thread median is 0.53% lower, with overlapping ranges; these local results do not establish universal scaling or tail-latency improvements. The separate unbuffered stripe-proxy gap remains a documented tradeoff, not a reason to sacrifice the measured buffered gains.

## Verification and PR build fix

- Release solution build: **0 warnings, 0 errors**. All **1,224 tests** pass (143 concurrency, 795 main, 286 scheduling), with no skips or relaxed assertions.
- All **72 unique/tied-priority trace hashes** and **160 rank/delay records** match original Git `6800ae0`. Source hashes also verify that every production concurrency `.cs` file matches the investigation's starting queue implementation.
- Native AOT publish and smoke pass for reference and compact value entries with default/custom comparers. Package auditing stays enabled; the earlier `NuGetAudit=false` publish workaround is no longer needed.
- PR #69's first CI attempt stopped during restore because existing SourceLink 8.0.0 brought in vulnerable `Microsoft.Build.Tasks.Git` 8.0.0. Updated the private build dependency `Microsoft.SourceLink.GitHub` to **10.0.403**, which resolves Tasks.Git/Common to 10.0.403. This follows the [official advisory](https://github.com/dotnet/sourcelink/security/advisories/GHSA-23fw-v26w-5fgq) and [published package dependencies](https://www.nuget.org/packages/Microsoft.SourceLink.GitHub/10.0.403); it does not change queue runtime dependencies or suppress audit checks.
- Performance coverage remains this ARM64 host/runtime, integer priorities and inexpensive comparers. Other JIT/runtime versions, architectures, reference priorities, expensive comparers, cold initialization and production tail latency remain unmeasured.

[Evidence and reproduction instructions](data/api-unbuffered-performance-2026-10-28/README.md) include raw measurements, rejected patches, JIT diagnostics, exact source hashes and verification output. The PR retains its original API changes and exact internal priority dispatch.
'''
Path('docs/benchmarks/2026-10-28-api-unbuffered-performance.md').write_text(text)
for folder in ['confirm','preservation']:
 if (r/folder/'summary.json').exists():(d/f'{folder}-summary.json').write_bytes((r/folder/'summary.json').read_bytes())
print('wrote report')
