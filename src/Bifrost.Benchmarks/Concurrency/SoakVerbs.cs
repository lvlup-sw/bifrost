// =============================================================================
// <copyright file="SoakVerbs.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Globalization;

namespace Bifrost.Benchmarks.Concurrency;

/// <summary>
/// The CLI verb handler for the consumer-shaped soak harness (design DR-8). Like the
/// throughput verbs, the soak is plain console code behind a dedicated verb — not
/// BenchmarkDotNet, which has no notion of a long mixed-arrival window with seconds-long
/// simulated work items:
/// <c>soak [--binding both|locking|multiqueue] [--workers 2,8] [--seconds 600] [--capacity 128]
/// [--min-work-ms 50] [--max-work-ms 5000] [--mix 20,30,50] [--burst-interval 30]
/// [--burst-size 64] [--boost-seconds 30] [--seed 42] [--out DIR]</c>.
/// </summary>
public static class SoakVerbs
{
    /// <summary>
    /// Runs the soak sweep with the DR-8 defaults, honoring the optional flag overrides.
    /// Writes <c>soak-classes.csv</c>, <c>soak-runs.csv</c>, and <c>soak.md</c>.
    /// </summary>
    /// <param name="args">The full command line; <c>args[0]</c> is the verb itself.</param>
    /// <returns>A task completing when the sweep and its artifacts are done.</returns>
    public static async Task RunSoakAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (!TryParse(args, out SoakBinding[] bindings, out int[] workers, out SoakConfig config, out string outputDirectory))
        {
            PrintUsage();
            Environment.ExitCode = 2;
            return;
        }

        Console.WriteLine(
            $"Running consumer-shaped soak (DR-8): bindings={string.Join('/', bindings)}, workers={{{string.Join(',', workers)}}}, " +
            $"seconds={config.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)}, capacity={config.Capacity}, " +
            $"work={config.MinWorkMs}-{config.MaxWorkMs}ms log-uniform, mix=" +
            $"{(config.InteractiveShare * 100).ToString("0.#", CultureInfo.InvariantCulture)}/" +
            $"{(config.DefaultShare * 100).ToString("0.#", CultureInfo.InvariantCulture)}/" +
            $"{(config.BatchShare * 100).ToString("0.#", CultureInfo.InvariantCulture)}, output='{outputDirectory}'");

        await SoakSweep.RunAllAsync(bindings, workers, config, outputDirectory).ConfigureAwait(false);

        Console.WriteLine($"Wrote soak-classes.csv, soak-runs.csv, and soak.md to '{outputDirectory}'.");
    }

    /// <summary>Parses the flag overrides; <c>false</c> (with a message on stderr) on any malformed input.</summary>
    /// <param name="args">The full command line; <c>args[0]</c> is the verb itself.</param>
    /// <param name="bindings">The bindings to soak.</param>
    /// <param name="workers">The consumer counts to soak per binding.</param>
    /// <param name="config">The workload shape.</param>
    /// <param name="outputDirectory">The artifact output directory.</param>
    private static bool TryParse(
        string[] args,
        out SoakBinding[] bindings,
        out int[] workers,
        out SoakConfig config,
        out string outputDirectory)
    {
        bindings = [SoakBinding.LockingPriority, SoakBinding.MultiQueuePriority];
        workers = [2, 8];
        config = new SoakConfig();
        outputDirectory = Path.Combine(Directory.GetCurrentDirectory(), "soak-results");

        for (int i = 1; i < args.Length; i++)
        {
            string flag = args[i];
            string? value = i + 1 < args.Length ? args[i + 1] : null;
            if (value is null)
            {
                Console.Error.WriteLine($"soak: option '{flag}' is missing its value.");
                return false;
            }

            i++;
            switch (flag.ToLowerInvariant())
            {
                case "--binding":
                    switch (value.ToLowerInvariant())
                    {
                        case "both":
                            bindings = [SoakBinding.LockingPriority, SoakBinding.MultiQueuePriority];
                            break;
                        case "locking":
                            bindings = [SoakBinding.LockingPriority];
                            break;
                        case "multiqueue":
                            bindings = [SoakBinding.MultiQueuePriority];
                            break;
                        default:
                            Console.Error.WriteLine($"soak: unknown binding '{value}' (expected both, locking, or multiqueue).");
                            return false;
                    }

                    break;

                case "--workers":
                {
                    string[] parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var parsed = new int[parts.Length];
                    for (int p = 0; p < parts.Length; p++)
                    {
                        if (!int.TryParse(parts[p], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed[p]) || parsed[p] < 1)
                        {
                            Console.Error.WriteLine($"soak: invalid worker count '{parts[p]}'.");
                            return false;
                        }
                    }

                    if (parsed.Length == 0)
                    {
                        Console.Error.WriteLine("soak: --workers needs at least one count.");
                        return false;
                    }

                    workers = parsed;
                    break;
                }

                case "--seconds":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                        || !double.IsFinite(seconds)
                        || seconds <= 0)
                    {
                        Console.Error.WriteLine($"soak: invalid --seconds '{value}'.");
                        return false;
                    }

                    config = config with { DurationSeconds = seconds };
                    break;

                case "--capacity":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int capacity) || capacity < 1)
                    {
                        Console.Error.WriteLine($"soak: invalid --capacity '{value}'.");
                        return false;
                    }

                    config = config with { Capacity = capacity };
                    break;

                case "--min-work-ms":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minWork) || minWork < 1)
                    {
                        Console.Error.WriteLine($"soak: invalid --min-work-ms '{value}'.");
                        return false;
                    }

                    config = config with { MinWorkMs = minWork };
                    break;

                case "--max-work-ms":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxWork) || maxWork < 1)
                    {
                        Console.Error.WriteLine($"soak: invalid --max-work-ms '{value}'.");
                        return false;
                    }

                    config = config with { MaxWorkMs = maxWork };
                    break;

                case "--mix":
                {
                    string[] parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length != 3
                        || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double interactive)
                        || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double @default)
                        || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double batch)
                        || !double.IsFinite(interactive) || !double.IsFinite(@default) || !double.IsFinite(batch)
                        || interactive < 0 || @default < 0 || batch < 0
                        || Math.Abs(interactive + @default + batch - 100.0) > 0.001)
                    {
                        Console.Error.WriteLine($"soak: invalid --mix '{value}' (expected three non-negative percentages summing to 100, e.g. 20,30,50).");
                        return false;
                    }

                    config = config with { InteractiveShare = interactive / 100.0, DefaultShare = @default / 100.0 };
                    break;
                }

                case "--burst-interval":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double burstInterval)
                        || !double.IsFinite(burstInterval)
                        || burstInterval < 0)
                    {
                        Console.Error.WriteLine($"soak: invalid --burst-interval '{value}'.");
                        return false;
                    }

                    config = config with { BurstIntervalSeconds = burstInterval };
                    break;

                case "--burst-size":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int burstSize) || burstSize < 0)
                    {
                        Console.Error.WriteLine($"soak: invalid --burst-size '{value}'.");
                        return false;
                    }

                    config = config with { BurstSize = burstSize };
                    break;

                case "--boost-seconds":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double boostSeconds)
                        || !double.IsFinite(boostSeconds)
                        || boostSeconds < 0)
                    {
                        Console.Error.WriteLine($"soak: invalid --boost-seconds '{value}'.");
                        return false;
                    }

                    config = config with { InteractiveBoostSeconds = boostSeconds };
                    break;

                case "--seed":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed))
                    {
                        Console.Error.WriteLine($"soak: invalid --seed '{value}'.");
                        return false;
                    }

                    config = config with { Seed = seed };
                    break;

                case "--out":
                    outputDirectory = value;
                    break;

                default:
                    Console.Error.WriteLine($"soak: unknown option '{flag}'.");
                    return false;
            }
        }

        if (config.MinWorkMs > config.MaxWorkMs)
        {
            Console.Error.WriteLine("soak: --min-work-ms must not exceed --max-work-ms.");
            return false;
        }

        return true;
    }

    /// <summary>Prints the verb's usage block to the console.</summary>
    private static void PrintUsage()
    {
        Console.WriteLine("Usage: soak [options]");
        Console.WriteLine("  --binding both|locking|multiqueue   Bindings to soak (default both)");
        Console.WriteLine("  --workers N[,N...]                  Consumer counts per binding (default 2,8)");
        Console.WriteLine("  --seconds S                         Soak window per run in seconds (default 600)");
        Console.WriteLine("  --capacity N                        Bounded queue capacity (default 128)");
        Console.WriteLine("  --min-work-ms N                     Min simulated work duration (default 50)");
        Console.WriteLine("  --max-work-ms N                     Max simulated work duration (default 5000)");
        Console.WriteLine("  --mix I,D,B                         Arrival mix percentages (default 20,30,50)");
        Console.WriteLine("  --burst-interval S                  Seconds between batch bursts (default 30; 0 disables)");
        Console.WriteLine("  --burst-size N                      Batch items per burst (default 64; 0 disables)");
        Console.WriteLine("  --boost-seconds S                   Interactive boost window (default 30)");
        Console.WriteLine("  --seed N                            Base RNG seed (default 42)");
        Console.WriteLine("  --out DIR                           Artifact directory (default ./soak-results)");
    }
}
