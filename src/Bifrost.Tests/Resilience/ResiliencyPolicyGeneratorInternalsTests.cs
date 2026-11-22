// =============================================================================
// <copyright file="ResiliencyPolicyGeneratorInternalsTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Reflection;

using Bifrost.Resilience;

using Microsoft.Extensions.Logging;

namespace Bifrost.Tests.Resilience;

/// <summary>
/// Covers the two <c>private static</c> helpers of
/// <see cref="ResiliencyPolicyGenerator"/> — <c>CalculateRetryDelay</c> and
/// <c>LogRetryAttempt</c> — whose branch arms are not deterministically reachable
/// through the public Polly pipeline (DR-6, task-20).
/// </summary>
/// <remarks>
/// <para>
/// <c>CalculateRetryDelay</c> uses <see cref="Random.Shared"/> jitter (line 241), so the
/// negative-delay clamp arm cannot be hit reliably from the public surface. These helpers
/// are invoked directly by reflection — the test project opts out of AOT, so reflection is
/// permitted here — with controlled <see cref="ResiliencySettings"/> to exercise BOTH the
/// exponential/fixed backoff arms, the negative-delay clamp arm, and BOTH the
/// final/non-final log-level arms.
/// </para>
/// </remarks>
[Property("Category", "Unit")]
public class ResiliencyPolicyGeneratorInternalsTests
{
    /// <summary>
    /// Reflects the <c>private static TimeSpan CalculateRetryDelay(ResiliencySettings, int)</c>
    /// helper.
    /// </summary>
    private static readonly MethodInfo CalculateRetryDelayMethod =
        typeof(ResiliencyPolicyGenerator).GetMethod(
            "CalculateRetryDelay",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("CalculateRetryDelay not found.");

    /// <summary>
    /// Reflects the
    /// <c>private static void LogRetryAttempt(ILogger, ResiliencySettings, Exception, TimeSpan, int, Context)</c>
    /// helper.
    /// </summary>
    private static readonly MethodInfo LogRetryAttemptMethod =
        typeof(ResiliencyPolicyGenerator).GetMethod(
            "LogRetryAttempt",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("LogRetryAttempt not found.");

    /// <summary>
    /// Exponential-backoff arm: with <see cref="ResiliencySettings.UseExponentialBackoff"/>
    /// true, the base delay grows as 2^attempt seconds. Asserting that attempt 4
    /// (16 s base) lands far above attempt 0 (1 s base) — well beyond the ±100 ms jitter —
    /// pins the exponential branch deterministically.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task CalculateRetryDelay_ExponentialBackoff_GrowsWithAttempt()
    {
        var settings = new ResiliencySettings { UseExponentialBackoff = true };

        var delayAttempt0 = InvokeCalculateRetryDelay(settings, 0);
        var delayAttempt4 = InvokeCalculateRetryDelay(settings, 4);

        await Assert.That(delayAttempt0).IsGreaterThan(TimeSpan.FromMilliseconds(800));
        await Assert.That(delayAttempt0).IsLessThan(TimeSpan.FromMilliseconds(1200));
        await Assert.That(delayAttempt4).IsGreaterThan(TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Fixed-interval arm: with <see cref="ResiliencySettings.UseExponentialBackoff"/>
    /// false, the base delay is a constant <see cref="ResiliencySettings.RetryIntervalSeconds"/>
    /// regardless of attempt number — attempts 0 and 9 both land near the fixed 10 s base
    /// (within the ±100 ms jitter), pinning the fixed branch.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task CalculateRetryDelay_FixedInterval_IndependentOfAttempt()
    {
        var settings = new ResiliencySettings
        {
            UseExponentialBackoff = false,
            RetryIntervalSeconds = 10,
        };

        var delayAttempt0 = InvokeCalculateRetryDelay(settings, 0);
        var delayAttempt9 = InvokeCalculateRetryDelay(settings, 9);

        await Assert.That(delayAttempt0).IsGreaterThan(TimeSpan.FromMilliseconds(9800));
        await Assert.That(delayAttempt0).IsLessThan(TimeSpan.FromMilliseconds(10200));
        await Assert.That(delayAttempt9).IsGreaterThan(TimeSpan.FromMilliseconds(9800));
        await Assert.That(delayAttempt9).IsLessThan(TimeSpan.FromMilliseconds(10200));
    }

    /// <summary>
    /// Negative-delay clamp arm: with a fixed interval of 0 seconds the base delay is zero,
    /// so the <c>Random.Shared.Next(-100, 100)</c> jitter drives <c>calculatedDelay</c>
    /// non-positive whenever the draw is ≤ 0 — the ternary's else arm then floors the result
    /// at 100 ms. Across many invocations the clamp MUST be observed at least once, and the
    /// return is never non-positive — covering the clamp branch and proving the floor.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    /// <remarks>
    /// <para>
    /// A fixed interval of 0 s gives <c>baseDelay == TimeSpan.Zero</c>, and the jitter is in
    /// [-100, 99] ms. Each draw is non-positive with a probability of about 101/200. Over about 10k draws,
    /// the probability that no draw is non-positive is vanishingly small. Thus the clamp arm runs
    /// deterministically in practice, and the test asserts that the result never falls to zero or below.
    /// </para>
    /// <para>
    /// When <c>baseDelay</c> is zero, the clamp arm and the degenerate positive-jitter arm are the only
    /// outcomes. In both cases the returned delay is strictly positive.
    /// </para>
    /// </remarks>
    [Test]
    public async Task CalculateRetryDelay_NonPositiveBaseAndJitter_ClampsTo100ms()
    {
        var settings = new ResiliencySettings
        {
            UseExponentialBackoff = false,
            RetryIntervalSeconds = 0,
        };

        var clampObserved = false;

        for (var i = 0; i < 10_000; i++)
        {
            var delay = InvokeCalculateRetryDelay(settings, 0);

            await Assert.That(delay).IsGreaterThan(TimeSpan.Zero);

            if (delay == TimeSpan.FromMilliseconds(100))
            {
                clampObserved = true;
            }
        }

        await Assert.That(clampObserved).IsTrue();
    }

    /// <summary>
    /// Final-attempt log arm: when the retry count equals
    /// <see cref="ResiliencySettings.RetryCount"/> the helper logs at
    /// <see cref="LogLevel.Error"/> with the "Retry limit reached" message — the final
    /// branch of both ternaries.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task LogRetryAttempt_FinalAttempt_LogsError()
    {
        var settings = new ResiliencySettings { RetryCount = 3 };
        var recorder = new RecordingLogger();

        InvokeLogRetryAttempt(
            recorder,
            settings,
            new HttpRequestException("boom"),
            TimeSpan.FromSeconds(2),
            retryCount: 3,
            new Polly.Context("op-final"));

        await Assert.That(recorder.Entries).HasSingleItem();
        var entry = recorder.Entries[0];
        await Assert.That(entry.Level).IsEqualTo(LogLevel.Error);
        await Assert.That(entry.Message).Contains("Retry limit");
    }

    /// <summary>
    /// Non-final-attempt log arm: when the retry count is below
    /// <see cref="ResiliencySettings.RetryCount"/> the helper logs at
    /// <see cref="LogLevel.Warning"/> with the per-attempt "Retry N of M" message — the
    /// non-final branch of both ternaries.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task LogRetryAttempt_NonFinalAttempt_LogsWarning()
    {
        var settings = new ResiliencySettings { RetryCount = 3 };
        var recorder = new RecordingLogger();

        InvokeLogRetryAttempt(
            recorder,
            settings,
            new HttpRequestException("boom"),
            TimeSpan.FromSeconds(2),
            retryCount: 1,
            new Polly.Context("op-nonfinal"));

        await Assert.That(recorder.Entries).HasSingleItem();
        var entry = recorder.Entries[0];
        await Assert.That(entry.Level).IsEqualTo(LogLevel.Warning);
        await Assert.That(entry.Message).Contains("Retry 1 of 3");
    }

    private static TimeSpan InvokeCalculateRetryDelay(ResiliencySettings settings, int retryAttempt) =>
        (TimeSpan)CalculateRetryDelayMethod.Invoke(null, [settings, retryAttempt])!;

    private static void InvokeLogRetryAttempt(
        ILogger logger,
        ResiliencySettings settings,
        Exception exception,
        TimeSpan timeSpan,
        int retryCount,
        Polly.Context context) =>
        LogRetryAttemptMethod.Invoke(null, [logger, settings, exception, timeSpan, retryCount, context]);

    /// <summary>
    /// A minimal <see cref="ILogger"/> that records the level and formatted message of each
    /// <see cref="ILogger.Log{TState}"/> call, so the log-level branch under test can be
    /// asserted deterministically.
    /// </summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            this.Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
