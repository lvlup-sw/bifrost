// =============================================================================
// <copyright file="BifrostHttpResilienceTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Net;

using Bifrost.Resilience;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Polly;

using TUnit.Core;

namespace Bifrost.Tests.Resilience;

/// <summary>
/// Tests for <see cref="BifrostHttpResilienceOptions.ResolvePolicy"/> — the per-HttpClient-name policy
/// selection that lets a single default handler serve differently-tuned clients from configuration.
/// </summary>
[Property("Category", "Unit")]
public class BifrostHttpResilienceOptionsTests
{
    /// <summary>A named client resolves its own policy entry.</summary>
    [Test]
    public async Task ResolvePolicy_KnownClient_ReturnsNamedPolicy()
    {
        var named = new BifrostHttpResiliencePolicy { MaxRetries = 0 };
        var options = new BifrostHttpResilienceOptions { Default = new BifrostHttpResiliencePolicy { MaxRetries = 3 } };
        options.Policies["streaming"] = named;

        await Assert.That(options.ResolvePolicy("streaming")).IsSameReferenceAs(named);
    }

    /// <summary>Client-name matching is case-insensitive.</summary>
    [Test]
    public async Task ResolvePolicy_IsCaseInsensitive()
    {
        var named = new BifrostHttpResiliencePolicy { MaxRetries = 0 };
        var options = new BifrostHttpResilienceOptions();
        options.Policies["streaming"] = named;

        await Assert.That(options.ResolvePolicy("STREAMING")).IsSameReferenceAs(named);
    }

    /// <summary>An unmatched or empty client name falls back to <see cref="BifrostHttpResilienceOptions.Default"/>.</summary>
    [Test]
    [Arguments("no-entry")]
    [Arguments("")]
    public async Task ResolvePolicy_UnknownOrEmpty_ReturnsDefault(string clientName)
    {
        var options = new BifrostHttpResilienceOptions();
        options.Policies["something-else"] = new BifrostHttpResiliencePolicy();

        await Assert.That(options.ResolvePolicy(clientName)).IsSameReferenceAs(options.Default);
    }
}

/// <summary>
/// Integration tests for <see cref="BifrostHttpResilienceExtensions.AddBifrostResilienceHandler"/> proving
/// the handler is real (retries transient failures) and that per-client-name config selects the policy —
/// a client configured with <see cref="BifrostHttpResiliencePolicy.MaxRetries"/> = 0 does NOT retry, while
/// a client on the <see cref="BifrostHttpResilienceOptions.Default"/> policy does.
/// </summary>
[Property("Category", "Unit")]
public class BifrostHttpResilienceHandlerTests
{
    private const string DefaultClient = "default-client";
    private const string NoRetryClient = "no-retry-client";

    private static BifrostHttpResiliencePolicy FastRetryDefault() => new()
    {
        MaxRetries = 3,
        RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        UseExponentialBackoff = false,
        UseJitter = false,
    };

    // A ServiceCollection mirroring a host: logging plus an (empty) IConfiguration, since
    // AddBifrostResilienceHandler binds Bifrost:Resilience from configuration and so requires IConfiguration.
    private static ServiceCollection NewHostServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        return services;
    }

    /// <summary>
    /// When the handler is registered centrally via <c>ConfigureHttpClientDefaults</c>, every client shares
    /// the single (empty-named) pipeline built from <see cref="BifrostHttpResilienceOptions.Default"/>, so a
    /// transient 503 is retried to a 200. Per-client policy resolution is NOT available on this path (the
    /// shared pipeline has no per-client name) — that is what per-client registration is for.
    /// </summary>
    [Test]
    public async Task DefaultsRegistration_AppliesDefaultPolicyUniformly_Retries()
    {
        var services = NewHostServices();
        services.Configure<BifrostHttpResilienceOptions>(o => o.Default = FastRetryDefault());
        services.ConfigureHttpClientDefaults(builder => builder.AddBifrostResilienceHandler());

        var stub = new StubPrimaryHandler();
        stub.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        stub.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK));
        services.AddHttpClient(DefaultClient).ConfigurePrimaryHttpMessageHandler(() => stub);

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(DefaultClient);
        var response = await client.GetAsync("https://example.test/health", CancellationToken.None).ConfigureAwait(false);

        await Assert.That((int)response.StatusCode).IsEqualTo(200);
        await Assert.That(stub.TotalCallCount).IsGreaterThanOrEqualTo(2);
    }

    /// <summary>
    /// Per-client registration honors that client's keyed policy: a client whose policy sets
    /// <see cref="BifrostHttpResiliencePolicy.MaxRetries"/> = 0 surfaces a transient 503 directly (one call,
    /// no retry), unlike the retrying Default — the config-only opt-out an SSE/streaming client uses.
    /// </summary>
    [Test]
    public async Task PerClientRegistration_NoRetryPolicy_IsNotRetried()
    {
        var services = NewHostServices();
        services.Configure<BifrostHttpResilienceOptions>(options =>
        {
            options.Default = FastRetryDefault();
            options.Policies[NoRetryClient] = new BifrostHttpResiliencePolicy { MaxRetries = 0, EnableCircuitBreaker = false };
        });

        var stub = new StubPrimaryHandler();
        stub.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        stub.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK));

        services.AddHttpClient(NoRetryClient)
            .AddBifrostResilienceHandler()
            .ConfigurePrimaryHttpMessageHandler(() => stub);

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(NoRetryClient);
        var response = await client.GetAsync("https://example.test/health", CancellationToken.None).ConfigureAwait(false);

        await Assert.That((int)response.StatusCode).IsEqualTo(503);
        await Assert.That(stub.TotalCallCount).IsEqualTo(1);
    }

    /// <summary>
    /// The platform combines the client and pipeline names as <c>{client}-{pipeline}</c>; the client name
    /// is recovered by stripping the pipeline suffix (empty for the default client).
    /// </summary>
    [Test]
    [Arguments("no-retry-client-bifrost-http", "bifrost-http", "no-retry-client")]
    [Arguments("-bifrost-http", "bifrost-http", "")]
    [Arguments("control-plane-streaming-bifrost-http", "bifrost-http", "control-plane-streaming")]
    public async Task GetClientName_StripsPipelineSuffix(string builderName, string pipelineName, string expected)
    {
        await Assert.That(BifrostHttpResilienceExtensions.GetClientName(builderName, pipelineName))
            .IsEqualTo(expected);
    }

    /// <summary>
    /// Proves the Aspire-style composition: a blanket <see cref="BifrostHttpResilienceOptions.Default"/> via
    /// ConfigureHttpClientDefaults (retries), with a single client overriding it by
    /// <c>RemoveAllResilienceHandlers()</c> + a per-client keyed handler (no-retry). The overriding client
    /// must observe the 503 directly (its keyed policy wins), while a sibling client still retries on the
    /// blanket default — i.e. RemoveAllResilienceHandlers on one client does NOT leak to others.
    /// </summary>
    [Test]
    public async Task BlanketDefault_WithPerClientKeyedOverride_BothResolveCorrectly()
    {
        var services = NewHostServices();
        services.Configure<BifrostHttpResilienceOptions>(options =>
        {
            options.Default = FastRetryDefault();
            options.Policies[NoRetryClient] = new BifrostHttpResiliencePolicy { MaxRetries = 0, EnableCircuitBreaker = false };
        });

        // Blanket default for every client.
        services.ConfigureHttpClientDefaults(builder => builder.AddBifrostResilienceHandler());

        var defaultStub = new StubPrimaryHandler();
        defaultStub.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        defaultStub.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK));
        services.AddHttpClient(DefaultClient).ConfigurePrimaryHttpMessageHandler(() => defaultStub);

        var overrideStub = new StubPrimaryHandler();
        overrideStub.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        overrideStub.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK));
        // Per-client keyed override — just call AddBifrostResilienceHandler(); it strips the inherited
        // blanket default internally, so application code never writes RemoveAllResilienceHandlers.
        services.AddHttpClient(NoRetryClient)
            .AddBifrostResilienceHandler()
            .ConfigurePrimaryHttpMessageHandler(() => overrideStub);

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        var overrideResponse = await factory.CreateClient(NoRetryClient)
            .GetAsync("https://example.test/health", CancellationToken.None).ConfigureAwait(false);
        var defaultResponse = await factory.CreateClient(DefaultClient)
            .GetAsync("https://example.test/health", CancellationToken.None).ConfigureAwait(false);

        // Overriding client: keyed no-retry policy wins → 503 surfaces, one call.
        await Assert.That((int)overrideResponse.StatusCode).IsEqualTo(503);
        await Assert.That(overrideStub.TotalCallCount).IsEqualTo(1);
        // Sibling client: unaffected, still retries on the blanket default → 200.
        await Assert.That((int)defaultResponse.StatusCode).IsEqualTo(200);
        await Assert.That(defaultStub.TotalCallCount).IsGreaterThanOrEqualTo(2);
    }

    /// <summary>
    /// AddBifrostResilienceHandler binds <c>Bifrost:Resilience</c> from configuration, so the Default and
    /// keyed policies come straight from config with no explicit binding in consumer code.
    /// </summary>
    [Test]
    public async Task Configuration_BindsDefaultAndKeyedPolicies()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bifrost:Resilience:Default:MaxRetries"] = "5",
                ["Bifrost:Resilience:Policies:control-plane-streaming:MaxRetries"] = "0",
                ["Bifrost:Resilience:Policies:control-plane-streaming:TotalRequestTimeout"] = "01:00:00",
            })
            .Build();

        var services = NewHostServices();
        services.AddSingleton<IConfiguration>(config);
        services.AddHttpClient("any").AddBifrostResilienceHandler();

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<BifrostHttpResilienceOptions>>().CurrentValue;

        await Assert.That(options.Default.MaxRetries).IsEqualTo(5);
        var streaming = options.ResolvePolicy("control-plane-streaming");
        await Assert.That(streaming.MaxRetries).IsEqualTo(0);
        await Assert.That(streaming.TotalRequestTimeout).IsEqualTo(TimeSpan.FromHours(1));
    }

    /// <summary>Queued primary handler: each call dequeues the next pre-loaded response, else 200.</summary>
    private sealed class StubPrimaryHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        private int _totalCallCount;

        public int TotalCallCount => _totalCallCount;

        public void EnqueueResponse(HttpResponseMessage response) => _responses.Enqueue(response);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _totalCallCount);
            var response = _responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.OK);
            return Task.FromResult(response);
        }
    }
}
