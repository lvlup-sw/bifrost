// =============================================================================
// <copyright file="SeqlockTearTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================
// Ported from lvlup-sw/DataFerry@2bf0456 (src/DataFerry.Tests/Concurrency/MultiQueue/SeqlockTearTests.cs)

using Bifrost.Concurrency.MultiQueue;

namespace Bifrost.Tests.Concurrency.MultiQueue;

/// <summary>
/// Write-storm stress test proving the cached-top seqlock never returns a torn
/// <c>TPriority</c> value under concurrent publication (DR-4).
/// </summary>
/// <remarks>
/// <para>
/// <b>How tearing is made observable.</b> The priority is a 32-byte struct of four
/// <see cref="ulong"/> words carrying a self-checking invariant
/// (<c>B == ~A</c>, <c>C == A * 31</c>, <c>D == A ^ B ^ C</c>). A 32-byte struct cannot be
/// written or read atomically, so if the seqlock's version validation were broken, a reader
/// overlapping a writer would observe a mix of two publications — and the invariant would fail.
/// One writer publishes random consistent values in a tight loop while four readers validate
/// every successful snapshot for two seconds.
/// </para>
/// <para>
/// <b>Kill-probe (RED witness).</b> This test was verified to DETECT tearing before being
/// trusted to assert its absence: with the seqlock's second version check disabled in
/// <c>SubQueue.TryReadTop</c> (mutation: accept the snapshot unconditionally instead of
/// re-validating <c>before == after</c>), this test fails within the two-second storm with
/// thousands of inconsistent reads. With the real seqlock restored it passes with zero torn
/// reads. The mutation procedure is recorded in the task notes; re-run it after any change to
/// <c>PublishTop</c>/<c>TryReadTop</c>.
/// </para>
/// <para>
/// The non-vacuousness floor asserts the readers actually exercised the validated-read path
/// (millions of reads in practice; the floor is deliberately three orders of magnitude lower
/// so slow CI machines do not flake).
/// </para>
/// </remarks>
public class SeqlockTearTests
{
    private const int ReaderCount = 4;
    private const long NonVacuousReadFloor = 10_000;
    private static readonly TimeSpan StormDuration = TimeSpan.FromSeconds(2);

    /// <summary>
    /// One writer storms <c>PublishTop</c> with internally consistent 32-byte priorities while
    /// four readers validate every <c>TryReadTop</c> snapshot: zero torn (inconsistent) reads
    /// may be observed, and the validated-read path must be exercised beyond the
    /// non-vacuousness floor (DR-4).
    /// </summary>
    [Test]
    public async Task TryReadTop_UnderWriteStorm_NeverReturnsTornValue()
    {
        // Arrange — a sub-queue whose priority type makes torn reads self-evident.
        var subQueue = new SubQueue<string, TornDetectPriority>(comparer: null);
        using var storm = new CancellationTokenSource(StormDuration);

        long validatedReads = 0;
        long unknownReads = 0;
        long tornReads = 0;
        TornDetectPriority firstTornSample = default;
        object tornSampleGate = new();

        var writer = new Thread(() =>
        {
            // Seeded for reproducibility; values are random but each is internally consistent.
            var rng = new Random(12345);
            while (!storm.IsCancellationRequested)
            {
                var next = TornDetectPriority.FromSeed(unchecked((ulong)rng.NextInt64()));
                lock (subQueue.SyncLock)
                {
                    subQueue.PublishTop(next, empty: false);
                }
            }
        })
        { IsBackground = true, Name = "seqlock-storm-writer" };

        var readers = new Thread[ReaderCount];
        for (int i = 0; i < ReaderCount; i++)
        {
            readers[i] = new Thread(() =>
            {
                long localValidated = 0;
                long localUnknown = 0;

                while (!storm.IsCancellationRequested)
                {
                    if (subQueue.TryReadTop(out TornDetectPriority snapshot, out bool empty))
                    {
                        if (!empty)
                        {
                            if (!snapshot.IsConsistent)
                            {
                                lock (tornSampleGate)
                                {
                                    tornReads++;
                                    firstTornSample = snapshot;
                                }
                            }

                            localValidated++;
                        }
                    }
                    else
                    {
                        localUnknown++;
                    }
                }

                Interlocked.Add(ref validatedReads, localValidated);
                Interlocked.Add(ref unknownReads, localUnknown);
            })
            { IsBackground = true, Name = $"seqlock-storm-reader-{i}" };
        }

        // Act — run the storm for its bounded window, then join everything.
        writer.Start();
        foreach (var reader in readers)
        {
            reader.Start();
        }

        writer.Join();
        foreach (var reader in readers)
        {
            reader.Join();
        }

        // Assert — zero torn snapshots, and the readers genuinely exercised the validated path.
        await Assert.That(tornReads).IsEqualTo(0).Because(
            $"a torn read slipped through version validation; first torn sample: {firstTornSample} " +
            $"(validated={validatedReads:N0}, unknown={unknownReads:N0})");
        await Assert.That(validatedReads).IsGreaterThan(NonVacuousReadFloor).Because(
            "the storm must actually exercise the validated-read path, or this test proves nothing");
    }

    /// <summary>
    /// A 32-byte priority whose four words are mutually derivable, so any mixture of two
    /// distinct publications (a torn read) breaks at least one relation.
    /// </summary>
    /// <param name="A">The seed word.</param>
    /// <param name="B">Always <c>~A</c>.</param>
    /// <param name="C">Always <c>A * 31</c> (wrapping).</param>
    /// <param name="D">Always <c>A ^ B ^ C</c> — a checksum over the other three words.</param>
    public readonly record struct TornDetectPriority(ulong A, ulong B, ulong C, ulong D)
    {
        /// <summary>Gets a value indicating whether all word relations hold (i.e. the value is untorn).</summary>
        public bool IsConsistent
            => B == ~A
            && C == unchecked(A * 31UL)
            && D == (A ^ B ^ C);

        /// <summary>Builds an internally consistent value from a single seed word.</summary>
        /// <param name="seed">The seed for all four words.</param>
        /// <returns>A consistent 32-byte priority.</returns>
        public static TornDetectPriority FromSeed(ulong seed)
        {
            ulong b = ~seed;
            ulong c = unchecked(seed * 31UL);
            return new TornDetectPriority(seed, b, c, seed ^ b ^ c);
        }
    }
}
