// =============================================================================
// <copyright file="HttpFailureBoundaryTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Net;
using System.Threading.Channels;
using Bifrost.Resilience;
using Microsoft.Extensions.Time.Testing;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Bifrost.Tests.Resilience;

/// <summary>Checks the configured HTTP strategy chain across cancellation, timeout, and recovery.</summary>
public class HttpFailureBoundaryTests
{
    /// <summary>Caller cancellation propagates without starting another attempt.</summary>
    [Test]
    public async Task CallerCancellationDoesNotRetry()
    {
        var pipeline = Build(new FakeTimeProvider(), new BifrostHttpResiliencePolicy { EnableCircuitBreaker = false });
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var request = pipeline.ExecuteAsync(async token =>
        {
            Interlocked.Increment(ref attempts);
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.That(async () => await request.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false)).Throws<OperationCanceledException>();
        await Assert.That(attempts).IsEqualTo(1);
    }

    /// <summary>A GET attempt timeout can retry, but the outer total budget still ends the operation.</summary>
    [Test]
    public async Task TotalTimeoutBoundsAttemptRetries()
    {
        var clock = new FakeTimeProvider();
        var pipeline = Build(clock, new BifrostHttpResiliencePolicy
        {
            EnableCircuitBreaker = false, AttemptTimeout = TimeSpan.FromSeconds(10), TotalRequestTimeout = TimeSpan.FromSeconds(25),
            MaxRetries = 10, RetryBaseDelay = TimeSpan.Zero, UseJitter = false,
        });
        using var cleanup = new CancellationTokenSource();
        using var message = new HttpRequestMessage(HttpMethod.Get, "https://example.test/health");
        var context = ResilienceContextPool.Shared.Get(cleanup.Token);
        context.SetRequestMessage(message);
        var starts = Channel.CreateUnbounded<int>();
        var attempts = 0;
        var request = pipeline.ExecuteAsync(async attemptContext =>
        {
            starts.Writer.TryWrite(Interlocked.Increment(ref attempts));
            await Task.Delay(Timeout.InfiniteTimeSpan, attemptContext.CancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, context).AsTask();
        try
        {
            await starts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            clock.Advance(TimeSpan.FromSeconds(10));
            await starts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            clock.Advance(TimeSpan.FromSeconds(15));
            await Assert.That(async () => await request.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false)).Throws<TimeoutRejectedException>();
            await Assert.That(attempts).IsGreaterThanOrEqualTo(2);
            await Assert.That(attempts).IsLessThanOrEqualTo(3);
        }
        finally
        {
            await cleanup.CancelAsync().ConfigureAwait(false);

            // The pipeline reads and restores the context until the request finishes, so the context
            // goes back to the pool only after that. A request still running here stays out of the pool.
            await ((Task)request).WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (request.IsCompleted)
            {
                ResilienceContextPool.Shared.Return(context);
            }
        }
    }

    /// <summary>The breaker rejects calls while open and allows a successful half-open probe.</summary>
    [Test]
    public async Task CircuitBreakerOpensAndRecovers()
    {
        var clock = new FakeTimeProvider();
        var pipeline = Build(clock, new BifrostHttpResiliencePolicy
        {
            MaxRetries = 0, CircuitBreakerMinimumThroughput = 2, CircuitBreakerFailureRatio = 0.5,
            CircuitBreakerBreakDuration = TimeSpan.FromSeconds(5),
        });
        var attempts = 0;
        ValueTask<HttpResponseMessage> Send(CancellationToken token)
            => ValueTask.FromResult(new HttpResponseMessage(++attempts <= 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        using var first = await pipeline.ExecuteAsync(Send).ConfigureAwait(false);
        using var second = await pipeline.ExecuteAsync(Send).ConfigureAwait(false);
        await Assert.That(async () => await pipeline.ExecuteAsync(Send).ConfigureAwait(false)).Throws<BrokenCircuitException>();
        await Assert.That(attempts).IsEqualTo(2);
        clock.Advance(TimeSpan.FromSeconds(6));
        using var recovered = await pipeline.ExecuteAsync(Send).ConfigureAwait(false);
        await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(attempts).IsEqualTo(3);
    }

    private static ResiliencePipeline<HttpResponseMessage> Build(FakeTimeProvider clock, BifrostHttpResiliencePolicy policy)
    {
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage> { TimeProvider = clock };
        BifrostHttpResilienceExtensions.ConfigurePipeline(builder, policy);
        return builder.Build();
    }
}
