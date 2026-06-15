using System.Globalization;
using Bifrost.Benchmarks.Concurrency;
string label = args.Length > 0 ? args[0] : "run";
double win = args.Length > 1 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 3.0;
int trials = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 3;
var window = TimeSpan.FromSeconds(win);
var runner = new ThroughputRunner();
int[] threads = { 4, 16, 32 };
var combos = new (ThroughputWorkload wl, ThroughputTarget tg)[] {
    (ThroughputWorkload.UniformMixed5050, ThroughputTarget.MultiQueueRelaxed),
    (ThroughputWorkload.NarrowKeyRange,   ThroughputTarget.MultiQueueRelaxed),
};
Console.WriteLine("label,target,workload,threads,trial,window_s,total_ops,ops_per_sec");
foreach (var (wl, tg) in combos)
  foreach (int t in threads)
    for (int trial = 0; trial < trials; trial++) {
        var r = runner.Run(tg, wl, t, window);
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0},{1},{2},{3},{4},{5:0.###},{6},{7:0.##}", label, tg, wl, t, trial, window.TotalSeconds, r.TotalOps, r.OpsPerSecond));
    }
