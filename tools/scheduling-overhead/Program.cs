using System.Diagnostics;
using System.Text.Json;
using Bifrost.Scheduling.Registry;
using Bifrost.Scheduling.Core;
using Bifrost.Scheduling.Internal;

var results = new List<object>();
for (var repeat = 0; repeat < 5; repeat++)
{
    foreach (var keyed in repeat % 2 == 0 ? new[] { false, true } : new[] { true, false })
    {
        var gates = new JobMutationGates();
        var stripes = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
        var name = "single";
        var stripe = stripes[(uint)StringComparer.Ordinal.GetHashCode(name) % 64];
        for (var i = 0; i < 10000; i++) await Operation();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < 100000; i++) await Operation();
        results.Add(new { scenario = "uncontended", repeat, keyed, nanoseconds = Stopwatch.GetElapsedTime(start).TotalNanoseconds / 100000, bytes = (GC.GetAllocatedBytesForCurrentThread() - allocated) / 100000.0, retained = gates.Count });
        async ValueTask Operation()
        {
            if (keyed) { using var lease = await gates.EnterAsync(name); }
            else { await stripe.WaitAsync(); stripe.Release(); }
        }
    }
}
foreach (var scenario in new[] { "independent", "hot", "collision" })
foreach (var delay in new[] { 1, 10 })
for (var repeat = 0; repeat < 3; repeat++)
{
    var names = Enumerable.Range(0, 64).Select(i => scenario == "hot" ? "hot" : "job" + i).ToArray();
    if (scenario == "collision") names = Enumerable.Range(0, 100000).Select(i => "collision" + i).Where(n => (uint)StringComparer.Ordinal.GetHashCode(n) % 64 == 0).Take(64).ToArray();
    foreach (var keyed in repeat % 2 == 0 ? new[] { false, true } : new[] { true, false })
    {
        var gates = new JobMutationGates();
        var stripes = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
        var latencies = new double[640];
        var start = Stopwatch.GetTimestamp();
        await Task.WhenAll(names.Select(async (name, actor) =>
        {
            for (var iteration = 0; iteration < 10; iteration++)
            {
                var arrived = Stopwatch.GetTimestamp();
                if (keyed)
                {
                    using var lease = await gates.EnterAsync(name);
                    await Task.Delay(delay);
                }
                else
                {
                    var gate = stripes[(uint)StringComparer.Ordinal.GetHashCode(name) % 64];
                    await gate.WaitAsync();
                    try { await Task.Delay(delay); }
                    finally { gate.Release(); }
                }
                latencies[actor * 10 + iteration] = Stopwatch.GetElapsedTime(arrived).TotalMilliseconds;
            }
        }));
        Array.Sort(latencies);
        results.Add(new { scenario, delay, repeat, keyed, milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds, p50 = latencies[320], p99 = latencies[633], retained = gates.Count });
    }
}
foreach (var backlog in new[] { 1, 100, 1000, 1000000 })
for (var repeat = 0; repeat < 3; repeat++)
{
    var cadence = new CronCadence("* * * * *", null);
    var now = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
    var last = now.AddMinutes(-backlog);
    foreach (var reverse in repeat % 2 == 0 ? new[] { false, true } : new[] { true, false })
    {
        var start = Stopwatch.GetTimestamp();
        DateTimeOffset? answer = null;
        for (var iteration = 0; iteration < 10; iteration++)
        {
            if (reverse) answer = MissedFirePolicyApplier.ComputeMissedFires(cadence, last, now, MissedFirePolicy.Coalesce).Single();
            else
            {
                var cursor = last;
                while (cadence.ComputeNextFire(cursor, cursor) is { } next && next <= now) { answer = next; cursor = next; }
            }
        }
        if (answer != now) throw new InvalidOperationException("Wrong cron occurrence");
        results.Add(new { scenario = "cron", backlog, repeat, reverse, nanoseconds = Stopwatch.GetElapsedTime(start).TotalNanoseconds / 10 });
    }
}
Console.WriteLine(JsonSerializer.Serialize(new { runtime = Environment.Version.ToString(), processors = Environment.ProcessorCount, results }, new JsonSerializerOptions { WriteIndented = true }));
