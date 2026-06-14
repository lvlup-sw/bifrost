// =============================================================================
// <copyright file="SoakSweep.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;

namespace Bifrost.Benchmarks.Concurrency;

/// <summary>
/// Drives the consumer-shaped soak (design DR-8) over bindings × worker counts and writes the
/// results as CSV (machine-readable, one per-class file and one run-level file) and a Markdown
/// table file, each prefixed with an environment header so a result file is self-describing —
/// mirroring <see cref="ThroughputSweep"/>'s artifact pattern. The <c>soak</c> CLI verb calls
/// <see cref="RunAllAsync"/>.
/// </summary>
public static class SoakSweep
{
    /// <summary>
    /// Runs one soak per binding × worker count, printing each run's summary as it lands, and
    /// writes <c>soak-classes.csv</c>, <c>soak-runs.csv</c>, and <c>soak.md</c> into
    /// <paramref name="outputDirectory"/>. A full GC runs between soaks so one run's garbage
    /// never bleeds into the next run's collection counts.
    /// </summary>
    /// <param name="bindings">The bindings to soak, in order.</param>
    /// <param name="workerCounts">The consumer counts to soak per binding; each must be at least one.</param>
    /// <param name="config">The shared workload shape.</param>
    /// <param name="outputDirectory">The directory the artifacts are written into (created if absent).</param>
    /// <returns>Every <see cref="SoakResult"/> produced, in sweep order.</returns>
    public static async Task<IReadOnlyList<SoakResult>> RunAllAsync(
        IReadOnlyList<SoakBinding> bindings,
        IReadOnlyList<int> workerCounts,
        SoakConfig config,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(workerCounts);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(outputDirectory);

        var results = new List<SoakResult>();
        foreach (SoakBinding binding in bindings)
        {
            foreach (int workers in workerCounts)
            {
                Console.WriteLine($"[soak] {binding} workers={workers} seconds={config.DurationSeconds} capacity={config.Capacity} ...");

                var runner = new SoakRunner(binding, workers, config);
                SoakResult result = await runner.RunAsync().ConfigureAwait(false);
                results.Add(result);
                PrintRun(result);

                // Quiesce between runs so collection-count deltas stay per-run.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }

        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "soak-classes.csv"), BuildClassCsv(results));
        File.WriteAllText(Path.Combine(outputDirectory, "soak-runs.csv"), BuildRunCsv(results));
        File.WriteAllText(Path.Combine(outputDirectory, "soak.md"), BuildMarkdown(results));

        return results;
    }

    /// <summary>Prints one run's per-class table, occupancy/allocation lines, and starvation probe to the console.</summary>
    /// <param name="result">The run to print.</param>
    private static void PrintRun(SoakResult result)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {result.Binding} | workers={result.Workers} | {result.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s ===");
        Console.WriteLine("  class        offered  rejected  dispatched   off%   disp%     p50ms     p95ms     p99ms     maxms");
        foreach (SoakClassResult c in result.Classes)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {c.Class,-11} {c.Offered,8} {c.Rejected,9} {c.Dispatched,11} {c.OfferedShare * 100,6:0.0} {c.DispatchShare * 100,7:0.0} {c.WaitP50Ms,9:0.0} {c.WaitP95Ms,9:0.0} {c.WaitP99Ms,9:0.0} {c.WaitMaxMs,9:0.0}"));
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  occupancy: mean {result.MeanOccupancyPct:0.0}% max {result.MaxOccupancyPct:0.0}% in-band {result.InBandPct:0.0}% | residual {result.ResidualCount}"));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  alloc/min: mean {result.AllocBytesPerMinMean / 1048576.0:0.00} MiB (min {result.AllocBytesPerMinMin / 1048576.0:0.00}, max {result.AllocBytesPerMinMax / 1048576.0:0.00}) | gen0 {result.Gen0Collections} gen1 {result.Gen1Collections} gen2 {result.Gen2Collections}"));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  starvation probe: max batch wait {result.MaxBatchWaitSeconds:0.0}s vs boost window {result.BoostWindowSeconds:0.#}s (depth-adjusted bound {result.DepthAdjustedBoundSeconds:0.0}s) -> {(result.StarvationBoundHeld ? "HELD" : "EXCEEDED")}"));
        Console.WriteLine();
    }

    /// <summary>Builds the environment header lines (shared by the artifacts), each prefixed with <paramref name="commentPrefix"/>.</summary>
    /// <param name="commentPrefix">The comment prefix (<c>#</c> for CSV, <c>//</c> for the Markdown preamble).</param>
    private static IEnumerable<string> EnvironmentHeaderLines(string commentPrefix)
    {
        yield return $"{commentPrefix} Bifrost CPQ consumer-shaped soak (DR-8) — generated {DateTimeOffset.UtcNow:u}";
        yield return $"{commentPrefix} ProcessorCount: {Environment.ProcessorCount}";
        yield return $"{commentPrefix} OS: {RuntimeInformation.OSDescription}";
        yield return $"{commentPrefix} CPU arch: {RuntimeInformation.ProcessArchitecture}";
        yield return $"{commentPrefix} Runtime: {RuntimeInformation.FrameworkDescription}";
        yield return $"{commentPrefix} GC mode: {(GCSettings.IsServerGC ? "Server" : "Workstation")}, Concurrent: {GCSettings.LatencyMode}";
    }

    /// <summary>Serializes the per-class rows as CSV with a commented (<c>#</c>) environment header.</summary>
    /// <param name="results">The runs to serialize.</param>
    private static string BuildClassCsv(IReadOnlyList<SoakResult> results)
    {
        var sb = new StringBuilder();
        foreach (string line in EnvironmentHeaderLines("#"))
        {
            sb.AppendLine(line);
        }

        sb.AppendLine("Binding,Workers,Seconds,Class,Offered,Rejected,Dispatched,OfferedSharePct,DispatchSharePct,WaitP50Ms,WaitP95Ms,WaitP99Ms,WaitMaxMs");
        foreach (SoakResult r in results)
        {
            foreach (SoakClassResult c in r.Classes)
            {
                sb.Append(r.Binding).Append(',')
                  .Append(r.Workers.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(r.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                  .Append(c.Class).Append(',')
                  .Append(c.Offered.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(c.Rejected.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(c.Dispatched.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append((c.OfferedShare * 100).ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                  .Append((c.DispatchShare * 100).ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                  .Append(c.WaitP50Ms.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                  .Append(c.WaitP95Ms.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                  .Append(c.WaitP99Ms.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                  .Append(c.WaitMaxMs.ToString("0.##", CultureInfo.InvariantCulture))
                  .AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>Serializes the run-level rows (occupancy, allocation stability, starvation probe) as CSV.</summary>
    /// <param name="results">The runs to serialize.</param>
    private static string BuildRunCsv(IReadOnlyList<SoakResult> results)
    {
        var sb = new StringBuilder();
        foreach (string line in EnvironmentHeaderLines("#"))
        {
            sb.AppendLine(line);
        }

        sb.AppendLine("Binding,Workers,Seconds,Capacity,Residual,MeanOccupancyPct,MaxOccupancyPct,InBandPct,AllocBytesPerMinMean,AllocBytesPerMinMin,AllocBytesPerMinMax,Gen0,Gen1,Gen2,MaxBatchWaitSeconds,BoostWindowSeconds,DepthAdjustedBoundSeconds,StarvationBoundHeld");
        foreach (SoakResult r in results)
        {
            sb.Append(r.Binding).Append(',')
              .Append(r.Workers.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Capacity.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.ResidualCount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.MeanOccupancyPct.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.MaxOccupancyPct.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.InBandPct.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.AllocBytesPerMinMean.ToString("0", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.AllocBytesPerMinMin.ToString("0", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.AllocBytesPerMinMax.ToString("0", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Gen0Collections.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Gen1Collections.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Gen2Collections.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.MaxBatchWaitSeconds.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.BoostWindowSeconds.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.DepthAdjustedBoundSeconds.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.StarvationBoundHeld ? "true" : "false")
              .AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Serializes both tables as Markdown with the environment header as a fenced preamble.</summary>
    /// <param name="results">The runs to serialize.</param>
    private static string BuildMarkdown(IReadOnlyList<SoakResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Bifrost CPQ Consumer-Shaped Soak (DR-8)");
        sb.AppendLine();
        sb.AppendLine("```text");
        foreach (string line in EnvironmentHeaderLines("//"))
        {
            sb.AppendLine(line.Substring(3));
        }

        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Per-class queue-wait and fairness");
        sb.AppendLine();
        sb.AppendLine("| Binding | Workers | Window (s) | Class | Offered | Rejected | Dispatched | Offered % | Dispatch % | p50 (ms) | p95 (ms) | p99 (ms) | max (ms) |");
        sb.AppendLine("| --- | ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (SoakResult r in results)
        {
            foreach (SoakClassResult c in r.Classes)
            {
                sb.Append("| ").Append(r.Binding)
                  .Append(" | ").Append(r.Workers.ToString(CultureInfo.InvariantCulture))
                  .Append(" | ").Append(r.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture))
                  .Append(" | ").Append(c.Class)
                  .Append(" | ").Append(c.Offered.ToString("N0", CultureInfo.InvariantCulture))
                  .Append(" | ").Append(c.Rejected.ToString("N0", CultureInfo.InvariantCulture))
                  .Append(" | ").Append(c.Dispatched.ToString("N0", CultureInfo.InvariantCulture))
                  .Append(" | ").Append((c.OfferedShare * 100).ToString("0.0", CultureInfo.InvariantCulture))
                  .Append(" | ").Append((c.DispatchShare * 100).ToString("0.0", CultureInfo.InvariantCulture))
                  .Append(" | ").Append(c.WaitP50Ms.ToString("N0", CultureInfo.InvariantCulture))
                  .Append(" | ").Append(c.WaitP95Ms.ToString("N0", CultureInfo.InvariantCulture))
                  .Append(" | ").Append(c.WaitP99Ms.ToString("N0", CultureInfo.InvariantCulture))
                  .Append(" | ").Append(c.WaitMaxMs.ToString("N0", CultureInfo.InvariantCulture))
                  .AppendLine(" |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Run-level: occupancy, allocation stability, starvation probe");
        sb.AppendLine();
        sb.AppendLine("| Binding | Workers | Residual | Occ mean % | Occ max % | In-band % | Alloc MiB/min (mean) | Alloc MiB/min (min–max) | Gen0 | Gen1 | Gen2 | Max batch wait (s) | Boost window (s) | Depth-adj bound (s) | Bound held |");
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
        foreach (SoakResult r in results)
        {
            sb.Append("| ").Append(r.Binding)
              .Append(" | ").Append(r.Workers.ToString(CultureInfo.InvariantCulture))
              .Append(" | ").Append(r.ResidualCount.ToString(CultureInfo.InvariantCulture))
              .Append(" | ").Append(r.MeanOccupancyPct.ToString("0.0", CultureInfo.InvariantCulture))
              .Append(" | ").Append(r.MaxOccupancyPct.ToString("0.0", CultureInfo.InvariantCulture))
              .Append(" | ").Append(r.InBandPct.ToString("0.0", CultureInfo.InvariantCulture))
              .Append(" | ").Append((r.AllocBytesPerMinMean / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture))
              .Append(" | ").Append((r.AllocBytesPerMinMin / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture))
              .Append('–').Append((r.AllocBytesPerMinMax / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture))
              .Append(" | ").Append(r.Gen0Collections.ToString(CultureInfo.InvariantCulture))
              .Append(" | ").Append(r.Gen1Collections.ToString(CultureInfo.InvariantCulture))
              .Append(" | ").Append(r.Gen2Collections.ToString(CultureInfo.InvariantCulture))
              .Append(" | ").Append(r.MaxBatchWaitSeconds.ToString("0.0", CultureInfo.InvariantCulture))
              .Append(" | ").Append(r.BoostWindowSeconds.ToString("0.#", CultureInfo.InvariantCulture))
              .Append(" | ").Append(r.DepthAdjustedBoundSeconds.ToString("0.0", CultureInfo.InvariantCulture))
              .Append(" | ").Append(r.StarvationBoundHeld ? "HELD" : "EXCEEDED")
              .AppendLine(" |");
        }

        return sb.ToString();
    }
}
