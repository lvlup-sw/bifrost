// =============================================================================
// <copyright file="ThroughputSweep.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================
// Ported from lvlup-sw/DataFerry@2bf0456 (src/DataFerry.Benchmarks/Throughput/ThroughputSweep.cs);
// output headers rebranded from DataFerry to Bifrost.

using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;

namespace Bifrost.Benchmarks.Concurrency;

/// <summary>
/// Drives the full fixed-window throughput sweep over thread counts ×
/// <see cref="ThroughputWorkload"/>s × <see cref="ThroughputTarget"/>s and writes the results as both
/// a CSV (machine-readable) and a Markdown table (human-readable), each prefixed with an environment
/// header so a result file is self-describing. The <c>throughput</c> CLI verb calls
/// <see cref="RunAll(double, string)"/> with defaults.
/// </summary>
public static class ThroughputSweep
{
    /// <summary>
    /// Runs the sweep over a default thread-count ladder (1, 2, 4, …, up to 2×<see cref="Environment.ProcessorCount"/>),
    /// every workload, and every target, writing <c>throughput.csv</c> and <c>throughput.md</c> into
    /// <paramref name="outputDirectory"/>.
    /// </summary>
    /// <param name="windowSeconds">The per-run wall-clock window, in seconds.</param>
    /// <param name="outputDirectory">The directory the CSV and Markdown files are written into (created if absent).</param>
    public static void RunAll(double windowSeconds, string outputDirectory)
        => RunAll(windowSeconds, DefaultThreadCounts(), outputDirectory);

    /// <summary>
    /// Runs the sweep over the supplied <paramref name="threadCounts"/>, every workload, and every
    /// target, writing <c>throughput.csv</c> and <c>throughput.md</c> into <paramref name="outputDirectory"/>.
    /// </summary>
    /// <param name="windowSeconds">The per-run wall-clock window, in seconds.</param>
    /// <param name="threadCounts">The thread counts to sweep; each must be at least one.</param>
    /// <param name="outputDirectory">The directory the CSV and Markdown files are written into (created if absent).</param>
    /// <returns>Every <see cref="ThroughputResult"/> produced, in sweep order.</returns>
    public static IReadOnlyList<ThroughputResult> RunAll(double windowSeconds, int[] threadCounts, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(threadCounts);
        ArgumentNullException.ThrowIfNull(outputDirectory);

        var window = TimeSpan.FromSeconds(windowSeconds);
        var runner = new ThroughputRunner();
        var results = new List<ThroughputResult>();

        foreach (ThroughputTarget target in Enum.GetValues<ThroughputTarget>())
        {
            foreach (ThroughputWorkload workload in Enum.GetValues<ThroughputWorkload>())
            {
                foreach (int threadCount in threadCounts)
                {
                    results.Add(runner.Run(target, workload, threadCount, window));
                }
            }
        }

        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "throughput.csv"), BuildCsv(results));
        File.WriteAllText(Path.Combine(outputDirectory, "throughput.md"), BuildMarkdown(results));

        return results;
    }

    /// <summary>
    /// Runs the stickiness sweep for the weak regimes the stickiness work targets: the relaxed
    /// MultiQueue target across every workload and the supplied <paramref name="threadCounts"/>, for
    /// each stickiness level in <paramref name="stickinessLevels"/> (e.g. <c>{1, 2, 4, 8}</c>). Only
    /// the relaxed target is swept — the locking and strict targets do not sample sub-queues, so
    /// stickiness is inert for them. Writes <c>throughput-stickiness.csv</c> and <c>throughput-stickiness.md</c>.
    /// </summary>
    /// <param name="windowSeconds">The per-run wall-clock window, in seconds.</param>
    /// <param name="threadCounts">The thread counts to sweep (the weak regimes are typically 1 and 2).</param>
    /// <param name="stickinessLevels">The stickiness factors to sweep, e.g. <c>{1, 2, 4, 8}</c>.</param>
    /// <param name="outputDirectory">The directory the CSV and Markdown files are written into (created if absent).</param>
    /// <returns>Every <see cref="ThroughputResult"/> produced, in sweep order.</returns>
    public static IReadOnlyList<ThroughputResult> RunStickinessLadder(double windowSeconds, int[] threadCounts, int[] stickinessLevels, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(threadCounts);
        ArgumentNullException.ThrowIfNull(stickinessLevels);
        ArgumentNullException.ThrowIfNull(outputDirectory);

        var window = TimeSpan.FromSeconds(windowSeconds);
        var runner = new ThroughputRunner();
        var results = new List<ThroughputResult>();

        foreach (ThroughputWorkload workload in Enum.GetValues<ThroughputWorkload>())
        {
            foreach (int threadCount in threadCounts)
            {
                foreach (int stickiness in stickinessLevels)
                {
                    results.Add(runner.Run(ThroughputTarget.MultiQueueRelaxed, workload, threadCount, window, stickiness: stickiness));
                }
            }
        }

        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "throughput-stickiness.csv"), BuildCsv(results));
        File.WriteAllText(Path.Combine(outputDirectory, "throughput-stickiness.md"), BuildMarkdown(results));

        return results;
    }

    /// <summary>
    /// Runs the buffered-vs-unbuffered A/B for the relaxed MultiQueue target (design DR-8): the same
    /// dense contended workloads as <see cref="RunAll(double, int[], string)"/>, but only the relaxed
    /// target, swept across the supplied <paramref name="bufferCapacities"/> (e.g. <c>{0, 16}</c>) so
    /// each (workload, threadCount) pair is reported once per buffer capacity. Buffering is inert for
    /// the locking and strict targets, so they are excluded — this isolates the off-vs-on delta.
    /// Writes <c>throughput-buffered-ab.csv</c> and <c>throughput-buffered-ab.md</c>, each carrying a
    /// dedicated <c>BufferCapacity</c> column.
    /// </summary>
    /// <param name="windowSeconds">The per-run wall-clock window, in seconds.</param>
    /// <param name="threadCounts">The thread counts to sweep; each must be at least one.</param>
    /// <param name="bufferCapacities">The buffer capacities to sweep, e.g. <c>{0, 16}</c> (off vs. paper-optimum).</param>
    /// <param name="outputDirectory">The directory the CSV and Markdown files are written into (created if absent).</param>
    /// <returns>Every (buffer capacity, <see cref="ThroughputResult"/>) pair produced, in sweep order.</returns>
    public static IReadOnlyList<(int BufferCapacity, ThroughputResult Result)> RunBufferedAb(
        double windowSeconds,
        int[] threadCounts,
        int[] bufferCapacities,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(threadCounts);
        ArgumentNullException.ThrowIfNull(bufferCapacities);
        ArgumentNullException.ThrowIfNull(outputDirectory);

        var window = TimeSpan.FromSeconds(windowSeconds);
        var runner = new ThroughputRunner();
        var results = new List<(int BufferCapacity, ThroughputResult Result)>();

        foreach (ThroughputWorkload workload in Enum.GetValues<ThroughputWorkload>())
        {
            foreach (int threadCount in threadCounts)
            {
                foreach (int bufferCapacity in bufferCapacities)
                {
                    ThroughputResult result = runner.Run(
                        ThroughputTarget.MultiQueueRelaxed,
                        workload,
                        threadCount,
                        window,
                        bufferCapacity: bufferCapacity);
                    results.Add((bufferCapacity, result));
                }
            }
        }

        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "throughput-buffered-ab.csv"), BuildBufferedCsv(results));
        File.WriteAllText(Path.Combine(outputDirectory, "throughput-buffered-ab.md"), BuildBufferedMarkdown(results));

        return results;
    }

    /// <summary>
    /// The default thread-count ladder: powers of two from 1 up to and including 2×<see cref="Environment.ProcessorCount"/>.
    /// </summary>
    internal static int[] DefaultThreadCounts()
    {
        int max = Math.Max(1, Environment.ProcessorCount * 2);
        var counts = new List<int>();
        for (int c = 1; c <= max; c *= 2)
        {
            counts.Add(c);
        }

        // Ensure the exact 2×cores point is present even when it is not a power of two.
        if (counts[^1] != max)
        {
            counts.Add(max);
        }

        return counts.ToArray();
    }

    /// <summary>Builds the environment header lines (shared by both outputs), each prefixed with <paramref name="commentPrefix"/>.</summary>
    private static IEnumerable<string> EnvironmentHeaderLines(string commentPrefix)
    {
        yield return $"{commentPrefix} Bifrost CPQ throughput sweep — generated {DateTimeOffset.UtcNow:u}";
        yield return $"{commentPrefix} ProcessorCount: {Environment.ProcessorCount}";
        yield return $"{commentPrefix} OS: {RuntimeInformation.OSDescription}";
        yield return $"{commentPrefix} CPU arch: {RuntimeInformation.ProcessArchitecture}";
        yield return $"{commentPrefix} Runtime: {RuntimeInformation.FrameworkDescription}";
        yield return $"{commentPrefix} GC mode: {(GCSettings.IsServerGC ? "Server" : "Workstation")}, Concurrent: {GCSettings.LatencyMode}";
    }

    /// <summary>Serializes the results as CSV with a commented (<c>#</c>) environment header.</summary>
    private static string BuildCsv(IReadOnlyList<ThroughputResult> results)
    {
        var sb = new StringBuilder();
        foreach (string line in EnvironmentHeaderLines("#"))
        {
            sb.AppendLine(line);
        }

        sb.AppendLine("Target,Workload,ThreadCount,Stickiness,WindowSeconds,TotalOps,OpsPerSecond");
        foreach (ThroughputResult r in results)
        {
            sb.Append(r.Target).Append(',')
              .Append(r.Workload).Append(',')
              .Append(r.ThreadCount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Stickiness.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Window.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.TotalOps.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.OpsPerSecond.ToString("0.##", CultureInfo.InvariantCulture))
              .AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Serializes the results as a Markdown table with the environment header as a fenced preamble.</summary>
    private static string BuildMarkdown(IReadOnlyList<ThroughputResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Bifrost CPQ Throughput Sweep");
        sb.AppendLine();
        sb.AppendLine("```text");
        foreach (string line in EnvironmentHeaderLines("//"))
        {
            sb.AppendLine(line.Substring(3));
        }

        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("| Target | Workload | Threads | Stickiness | Window (s) | Total Ops | Ops/sec |");
        sb.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: |");
        foreach (ThroughputResult r in results)
        {
            sb.Append("| ").Append(r.Target)
              .Append(" | ").Append(r.Workload)
              .Append(" | ").Append(r.ThreadCount.ToString(CultureInfo.InvariantCulture))
              .Append(" | ").Append(r.Stickiness.ToString(CultureInfo.InvariantCulture))
              .Append(" | ").Append(r.Window.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture))
              .Append(" | ").Append(r.TotalOps.ToString("N0", CultureInfo.InvariantCulture))
              .Append(" | ").Append(r.OpsPerSecond.ToString("N0", CultureInfo.InvariantCulture))
              .AppendLine(" |");
        }

        return sb.ToString();
    }

    /// <summary>Serializes the buffered-A/B results as CSV with a commented (<c>#</c>) environment header and a <c>BufferCapacity</c> column.</summary>
    private static string BuildBufferedCsv(IReadOnlyList<(int BufferCapacity, ThroughputResult Result)> results)
    {
        var sb = new StringBuilder();
        foreach (string line in EnvironmentHeaderLines("#"))
        {
            sb.AppendLine(line);
        }

        sb.AppendLine("Target,Workload,ThreadCount,BufferCapacity,Stickiness,WindowSeconds,TotalOps,OpsPerSecond");
        foreach ((int bufferCapacity, ThroughputResult r) in results)
        {
            sb.Append(r.Target).Append(',')
              .Append(r.Workload).Append(',')
              .Append(r.ThreadCount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(bufferCapacity.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Stickiness.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Window.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.TotalOps.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.OpsPerSecond.ToString("0.##", CultureInfo.InvariantCulture))
              .AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Serializes the buffered-A/B results as a Markdown table with a <c>BufferCapacity</c> column and the environment header as a fenced preamble.</summary>
    private static string BuildBufferedMarkdown(IReadOnlyList<(int BufferCapacity, ThroughputResult Result)> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Bifrost CPQ Buffered-vs-Unbuffered Throughput A/B");
        sb.AppendLine();
        sb.AppendLine("```text");
        foreach (string line in EnvironmentHeaderLines("//"))
        {
            sb.AppendLine(line.Substring(3));
        }

        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("| Target | Workload | Threads | Buffer C | Stickiness | Window (s) | Total Ops | Ops/sec |");
        sb.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach ((int bufferCapacity, ThroughputResult r) in results)
        {
            sb.Append("| ").Append(r.Target)
              .Append(" | ").Append(r.Workload)
              .Append(" | ").Append(r.ThreadCount.ToString(CultureInfo.InvariantCulture))
              .Append(" | ").Append(bufferCapacity.ToString(CultureInfo.InvariantCulture))
              .Append(" | ").Append(r.Stickiness.ToString(CultureInfo.InvariantCulture))
              .Append(" | ").Append(r.Window.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture))
              .Append(" | ").Append(r.TotalOps.ToString("N0", CultureInfo.InvariantCulture))
              .Append(" | ").Append(r.OpsPerSecond.ToString("N0", CultureInfo.InvariantCulture))
              .AppendLine(" |");
        }

        return sb.ToString();
    }
}
