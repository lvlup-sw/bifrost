// =============================================================================
// <copyright file="ThroughputVerbs.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================
// Ported from lvlup-sw/DataFerry@2bf0456 (src/DataFerry.Benchmarks/Program.cs verb handlers);
// extracted into a helper class so Bifrost.Benchmarks' top-level Program.cs stays a thin dispatcher.

using System.Globalization;

namespace Bifrost.Benchmarks.Concurrency;

/// <summary>
/// The CLI verb handlers for the CPQ fixed-window throughput harness. BenchmarkDotNet deliberately
/// does not measure cross-thread throughput (the BCL's own equivalent benchmark is disabled for
/// instability), so the custom <see cref="ThroughputRunner"/> sweep runs behind dedicated verbs
/// instead of the <c>BenchmarkSwitcher</c>:
/// <c>throughput [windowSeconds] [outputDirectory]</c> and
/// <c>stickiness [windowSeconds] [outputDirectory]</c>.
/// </summary>
public static class ThroughputVerbs
{
    /// <summary>
    /// Runs the full throughput sweep with defaults, honoring optional positional overrides:
    /// <c>throughput [windowSeconds] [outputDirectory]</c>. Writes <c>throughput.csv</c>/<c>.md</c>.
    /// </summary>
    /// <param name="args">The full command line; <c>args[0]</c> is the verb itself.</param>
    public static void RunThroughputSweep(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        (double windowSeconds, string outputDirectory) = ParseWindowAndOutput(args);

        Console.WriteLine($"Running throughput sweep: window={windowSeconds}s, output='{outputDirectory}'");
        ThroughputSweep.RunAll(windowSeconds, outputDirectory);
        Console.WriteLine($"Wrote throughput.csv and throughput.md to '{outputDirectory}'.");
    }

    /// <summary>
    /// Runs the stickiness ladder over the weak regimes the stickiness work targets — the relaxed
    /// MultiQueue target at 1 and 2 threads across every workload, for <c>s ∈ {1, 2, 4, 8}</c>:
    /// <c>stickiness [windowSeconds] [outputDirectory]</c>. Writes <c>throughput-stickiness.csv</c>/<c>.md</c>.
    /// </summary>
    /// <param name="args">The full command line; <c>args[0]</c> is the verb itself.</param>
    public static void RunStickinessSweep(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        (double windowSeconds, string outputDirectory) = ParseWindowAndOutput(args);

        int[] threadCounts = [1, 2];
        int[] stickinessLevels = [1, 2, 4, 8];

        Console.WriteLine($"Running stickiness ladder: window={windowSeconds}s, threads={{1,2}}, s={{1,2,4,8}}, output='{outputDirectory}'");
        ThroughputSweep.RunStickinessLadder(windowSeconds, threadCounts, stickinessLevels, outputDirectory);
        Console.WriteLine($"Wrote throughput-stickiness.csv and throughput-stickiness.md to '{outputDirectory}'.");
    }

    /// <summary>Parses the optional <c>[windowSeconds] [outputDirectory]</c> positional overrides shared by both verbs.</summary>
    /// <param name="args">The full command line; <c>args[0]</c> is the verb itself.</param>
    /// <returns>The window in seconds and the output directory.</returns>
    private static (double WindowSeconds, string OutputDirectory) ParseWindowAndOutput(string[] args)
    {
        double windowSeconds = ThroughputRunner.DefaultWindow.TotalSeconds;
        if (args.Length > 1 && double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            windowSeconds = parsed;
        }

        string outputDirectory = args.Length > 2
            ? args[2]
            : Path.Combine(Directory.GetCurrentDirectory(), "throughput-results");

        return (windowSeconds, outputDirectory);
    }
}
