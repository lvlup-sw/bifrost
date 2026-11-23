// =============================================================================
// <copyright file="UnsafeHttpMethodRetryTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

using Bifrost.Resilience;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Polly.Timeout;

namespace Bifrost.Tests.Resilience;

/// <summary>
/// Spec for the HTTP resilience handler: a committed POST, PUT, PATCH, DELETE, or CONNECT
/// is not sent again when the attempt ends in a timeout, 408, 429, or 5xx.
/// </summary>
/// <remarks>
/// These tests fail while the handler retries those methods. Safe methods still use the
/// default retry count of three. Delays are shortened only where a case says so. The retry
/// predicate and the default count stay on the production path.
/// </remarks>
[Property("Category", "Unit")]
public class UnsafeHttpMethodRetryTests
{
    private const string ClientName = "mutating-client";
    private const string Payload = "sku=item-42";
    private static readonly string[] UnsafeMethods = ["POST", "PUT", "PATCH", "DELETE", "CONNECT"];
    private static readonly int[] TransientStatuses = [408, 429, 500, 502, 503, 504];

    /// <summary>
    /// The documented all-clients registration, with no policy overrides, replays a committed POST
    /// after a 503. The server must see that POST once.
    /// </summary>
    [Test]
    public async Task DefaultPolicy_AllClients_Post503_IsNotSentAgain()
    {
        var observed = await ObserveAsync(
            "POST",
            PeerBehavior.Status,
            status: 503,
            allClients: true,
            synchronous: false,
            shortenDelays: false).ConfigureAwait(false);

        await Assert.That(observed.Sent).IsEqualTo(observed.OneDelivery);
    }

    /// <summary>
    /// Each unsafe method is committed, then answered with every transient status the retry predicate handles.
    /// </summary>
    [Test]
    public async Task TransientStatusAfterCommit_IsNotSentAgain()
    {
        var failures = new List<string>();
        foreach (var method in UnsafeMethods)
        {
            foreach (var status in TransientStatuses)
            {
                try
                {
                    var observed = await ObserveAsync(method, PeerBehavior.Status, status, allClients: false, synchronous: false, shortenDelays: true).ConfigureAwait(false);
                    if (observed.Sent != observed.OneDelivery)
                    {
                        failures.Add($"{method} {status}: {observed.Sent}");
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    failures.Add($"{method} {status} threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        await Assert.That(string.Join(Environment.NewLine, failures)).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// The per-attempt timeout sits inside the retry strategy. A committed unsafe request that hits it is not sent again.
    /// </summary>
    [Test]
    public async Task AttemptTimeoutAfterCommit_IsNotSentAgain()
    {
        var failures = new List<string>();
        foreach (var method in UnsafeMethods)
        {
            try
            {
                var observed = await ObserveAsync(method, PeerBehavior.Timeout, status: 0, allClients: false, synchronous: false, shortenDelays: true).ConfigureAwait(false);
                if (observed.Sent != observed.OneDelivery)
                {
                    failures.Add($"{method} timeout: {observed.Sent}");
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add($"{method} timeout threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        await Assert.That(string.Join(Environment.NewLine, failures)).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// A peer that reads the body and then drops the connection produces <see cref="HttpRequestException"/>.
    /// That outcome is not a reason to send the unsafe method again.
    /// </summary>
    [Test]
    public async Task ConnectionResetAfterCommit_IsNotSentAgain()
    {
        var failures = new List<string>();
        foreach (var method in UnsafeMethods)
        {
            try
            {
                var observed = await ObserveAsync(method, PeerBehavior.Reset, status: 0, allClients: false, synchronous: false, shortenDelays: true).ConfigureAwait(false);
                if (observed.Sent != observed.OneDelivery)
                {
                    failures.Add($"{method} reset: {observed.Sent}");
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add($"{method} reset threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        await Assert.That(string.Join(Environment.NewLine, failures)).IsEqualTo(string.Empty);
    }

    /// <summary>The synchronous <see cref="HttpClient.Send(HttpRequestMessage, CancellationToken)"/> path uses the same pipeline.</summary>
    [Test]
    public async Task SynchronousSend_Post503_IsNotSentAgain()
    {
        var observed = await ObserveAsync("POST", PeerBehavior.Status, status: 503, allClients: false, synchronous: true, shortenDelays: true).ConfigureAwait(false);
        await Assert.That(observed.Sent).IsEqualTo(observed.OneDelivery);
    }

    /// <summary>GET and HEAD keep the default three retries when the peer returns 503.</summary>
    [Test]
    public async Task SafeMethods_TransientStatus_UseDefaultRetryCount()
    {
        var failures = new List<string>();
        foreach (var method in new[] { "GET", "HEAD" })
        {
            try
            {
                var observed = await ObserveAsync(method, PeerBehavior.Status, status: 503, allClients: true, synchronous: false, shortenDelays: true).ConfigureAwait(false);
                var expected = string.Join(" | ", Enumerable.Repeat(observed.OneDelivery, 4));
                if (observed.Sent != expected)
                {
                    failures.Add($"{method}: {observed.Sent}");
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add($"{method} threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        await Assert.That(string.Join(Environment.NewLine, failures)).IsEqualTo(string.Empty);
    }

    private static async Task<DeliveryReport> ObserveAsync(
        string method,
        PeerBehavior behavior,
        int status,
        bool allClients,
        bool synchronous,
        bool shortenDelays)
    {
        await using var peer = await LoopbackPeer.StartAsync(behavior, status).ConfigureAwait(false);
        await using var provider = CreateProvider(allClients, shortenDelays);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);
        using var request = new HttpRequestMessage(new HttpMethod(method), peer.ResourceUri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = method is "GET" or "HEAD" ? null : new ByteArrayContent(Encoding.UTF8.GetBytes(Payload)),
        };
        request.Headers.ExpectContinue = false;
        if (method == "CONNECT")
        {
            request.Headers.Host = peer.ResourceUri.Authority;
        }

        if (request.Content is not null)
        {
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        try
        {
            if (synchronous)
            {
#pragma warning disable VSTHRD103
                using var response = client.Send(request, CancellationToken.None);
#pragma warning restore VSTHRD103
            }
            else
            {
                using var response = await client.SendAsync(request, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (behavior != PeerBehavior.Status && ex is HttpRequestException or TimeoutRejectedException)
        {
        }

        var target = method == "CONNECT" ? peer.ResourceUri.Authority : peer.ResourceUri.AbsolutePath;
        var body = request.Content is null ? string.Empty : Payload;
        return new DeliveryReport(peer.Dump(), $"{method} {target} {body}");
    }

    private readonly record struct DeliveryReport(string Sent, string OneDelivery);

    private static ServiceProvider CreateProvider(bool allClients, bool shortenDelays)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        if (shortenDelays)
        {
            services.Configure<BifrostHttpResilienceOptions>(options =>
            {
                options.Default.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
                options.Default.UseExponentialBackoff = false;
                options.Default.UseJitter = false;
                options.Default.AttemptTimeout = TimeSpan.FromSeconds(1);
                options.Default.TotalRequestTimeout = TimeSpan.FromSeconds(15);
            });
        }

        if (allClients)
        {
            services.ConfigureHttpClientDefaults(builder => builder.AddBifrostResilienceHandler());
            services.AddHttpClient(ClientName);
        }
        else
        {
            services.AddHttpClient(ClientName).AddBifrostResilienceHandler();
        }

        return services.BuildServiceProvider();
    }

    private enum PeerBehavior
    {
        Status,
        Timeout,
        Reset,
    }

    /// <summary>Reads one HTTP/1.1 request per connection and applies the scripted outcome after the body arrives.</summary>
    private sealed class LoopbackPeer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly PeerBehavior _behavior;
        private readonly int _status;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly List<CapturedRequest> _requests = [];
        private readonly List<Task> _handlers = [];
        private readonly object _gate = new();
        private readonly Task _acceptLoop;

        private LoopbackPeer(TcpListener listener, PeerBehavior behavior, int status)
        {
            _listener = listener;
            _behavior = behavior;
            _status = status;
            _acceptLoop = AcceptLoopAsync();
        }

        public Uri ResourceUri { get; private init; } = null!;

        public static Task<LoopbackPeer> StartAsync(PeerBehavior behavior, int status)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var peer = new LoopbackPeer(listener, behavior, status)
            {
                ResourceUri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/orders/42"),
            };
            return Task.FromResult(peer);
        }

        public string Dump()
        {
            lock (_gate)
            {
                return _requests.Count == 0
                    ? "(no request reached the peer)"
                    : string.Join(" | ", _requests.Select(static request => $"{request.Method} {request.Target} {request.Body}"));
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
#pragma warning disable VSTHRD003
            await _acceptLoop.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            await _lifetime.CancelAsync().ConfigureAwait(false);
            Task[] handlers;
            lock (_gate)
            {
                handlers = [.. _handlers];
            }

            try
            {
                await Task.WhenAll(handlers).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or SocketException)
            {
            }

            _lifetime.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_lifetime.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or SocketException or OperationCanceledException)
                {
                    break;
                }

                var handler = HandleAsync(client);
                lock (_gate)
                {
                    _handlers.Add(handler);
                }
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    await using var stream = client.GetStream();
                    var header = await ReadHeaderAsync(stream, _lifetime.Token).ConfigureAwait(false);
                    var (method, target, contentLength) = ParseHeader(header.Text);
                    var body = await ReadBodyAsync(stream, header.Remainder, contentLength, _lifetime.Token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        _requests.Add(new CapturedRequest(method, target, Encoding.UTF8.GetString(body)));
                    }

                    if (_behavior == PeerBehavior.Reset)
                    {
                        client.LingerState = new LingerOption(true, 0);
                        return;
                    }

                    if (_behavior == PeerBehavior.Timeout)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), _lifetime.Token).ConfigureAwait(false);
                        return;
                    }

                    var reason = Reason(_status);
                    var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {_status} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response, _lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                {
                }
            }
        }

        private static string Reason(int status) => status switch
        {
            408 => "Request Timeout",
            429 => "Too Many Requests",
            500 => "Internal Server Error",
            502 => "Bad Gateway",
            503 => "Service Unavailable",
            504 => "Gateway Timeout",
            _ => "Error",
        };

        private static (string Method, string Target, int ContentLength) ParseHeader(string header)
        {
            var lines = header.Split("\r\n");
            var requestLine = lines[0].Split(' ');
            var contentLength = 0;
            foreach (var line in lines)
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line.AsSpan("Content-Length:".Length).Trim(), out var parsed))
                {
                    contentLength = parsed;
                }
            }

            return (requestLine[0], requestLine[1], contentLength);
        }

        private static async Task<HeaderRead> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
        {
            var header = new List<byte>(1024);
            var buffer = new byte[1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("The client closed the connection before the headers ended.");
                }

                for (var offset = 0; offset < read; offset++)
                {
                    header.Add(buffer[offset]);
                }

                for (var index = 0; index <= header.Count - 4; index++)
                {
                    if (header[index] == '\r' && header[index + 1] == '\n' && header[index + 2] == '\r' && header[index + 3] == '\n')
                    {
                        var headerLength = index + 4;
                        var remainder = header.GetRange(headerLength, header.Count - headerLength).ToArray();
                        return new HeaderRead(Encoding.ASCII.GetString([.. header.GetRange(0, headerLength)]), remainder);
                    }
                }

                if (header.Count > 65536)
                {
                    throw new IOException("The request headers exceeded the test limit.");
                }
            }
        }

        private static async Task<byte[]> ReadBodyAsync(Stream stream, byte[] remainder, int contentLength, CancellationToken cancellationToken)
        {
            if (contentLength == 0)
            {
                return [];
            }

            var body = new byte[contentLength];
            var copied = Math.Min(remainder.Length, contentLength);
            Buffer.BlockCopy(remainder, 0, body, 0, copied);
            while (copied < contentLength)
            {
                var read = await stream.ReadAsync(body.AsMemory(copied, contentLength - copied), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("The client closed the connection before the body ended.");
                }

                copied += read;
            }

            return body;
        }

        private readonly record struct HeaderRead(string Text, byte[] Remainder);

        private readonly record struct CapturedRequest(string Method, string Target, string Body);
    }
}