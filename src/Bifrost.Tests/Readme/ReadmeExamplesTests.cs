// =============================================================================
// <copyright file="ReadmeExamplesTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using Bifrost.Core;
using Bifrost.DependencyInjection;
using Bifrost.Hosting;
using Bifrost.Resilience;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using TUnit.Assertions.Enums;
using TUnit.Core;

namespace Bifrost.Tests.Readme;

/// <summary>
/// Compiles and runs every C# example in the repository <c>README.md</c> against the release.
/// </summary>
/// <remarks>
/// <para>
/// Each README code block is copied verbatim into one of the example methods below, with the
/// variables it uses (<c>services</c>, <c>writer</c>, <c>logger</c>, <c>turn</c>) declared around
/// it, so a README example that stops compiling breaks this project's build. The tests then run
/// each example and check that it does what the README says.
/// </para>
/// <para>
/// <see cref="ReadmeCodeBlocks_AreMirroredVerbatim"/> keeps the copies honest: it reads the
/// README and this file and fails when a README C# block is not found here, ignoring only
/// whitespace differences. When you change a README example, change its copy here too.
/// </para>
/// </remarks>
[Property("Category", "Unit")]
public partial class ReadmeExamplesTests
{
    private const int ReadmeCSharpBlockCount = 5;
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Every C# block in the README appears in this file, ignoring whitespace.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task ReadmeCodeBlocks_AreMirroredVerbatim()
    {
        var root = FindRepositoryRoot();
        var readme = await File.ReadAllTextAsync(Path.Combine(root, "README.md"));
        var mirror = Normalize(await File.ReadAllTextAsync(
            Path.Combine(root, "src", "Bifrost.Tests", "Readme", "ReadmeExamplesTests.cs")));

        var blocks = CSharpBlock().Matches(readme).Select(static m => m.Groups["code"].Value).ToList();

        await Assert.That(blocks.Count).IsEqualTo(ReadmeCSharpBlockCount);
        foreach (var block in blocks)
        {
            await Assert.That(mirror.Contains(Normalize(block), StringComparison.Ordinal)).IsTrue()
                .Because($"README block is not mirrored in ReadmeExamplesTests.cs:{Environment.NewLine}{block}");
        }
    }

    /// <summary>The quick start registers a handler and a hosted service, and admission reports acceptance.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task QuickStart_RegistersAndRunsWork()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ResizeLog>();

        services.AddWorkOrchestrator<ResizeImage>(options =>
            {
                options.Capacity = 256;
                options.WorkerCount = 4;
            })
            .WithHandler<ResizeImage, ResizeImageHandler>()
            .Build();
        services.AddWorkOrchestratorHostedService<ResizeImage>();

        await using var provider = services.BuildServiceProvider();
        var writer = provider.GetRequiredService<IWorkWriter<ResizeImage>>();
        var logger = provider.GetRequiredService<ILogger<ReadmeExamplesTests>>();
        var imageId = Guid.NewGuid();

        var result = await writer.EnqueueAsync(new ResizeImage(imageId));
        if (!result.IsAccepted)
        {
            logger.LogWarning("Resize {ImageId} was rejected: {Reason}", imageId, result.Reason);
        }

        await Assert.That(result.IsAccepted).IsTrue();
        await Assert.That(await provider.GetRequiredService<ResizeLog>().Resized.Task.WaitAsync(WaitTimeout)).IsEqualTo(imageId);
        await Assert.That(provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<WorkOrchestratorHostedService<ResizeImage>>().Count()).IsEqualTo(1);
    }

    /// <summary>
    /// The keyed example runs a session's turns one at a time in admission order, applies the
    /// per-key limit, and maps each rejection reason to the README's reply.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task KeyedDispatch_RunsSessionInOrderAndRejectsOverKeyLimit()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TurnRunner>();

        services.AddWorkOrchestrator<Turn>(options => options.Capacity = 512)
            .WithKeyedDispatch(
                static turn => (turn.TenantId, turn.SessionId),
                maxPendingPerKey: 8,
                maxConcurrencyPerKey: 1)
            .WithScopedHandler(static async (services, turn, ct) =>
            {
                var runner = services.GetRequiredService<TurnRunner>();
                await runner.RunAsync(turn, ct);
            })
            .WithHandlerResilience(options => options.AttemptTimeout = TimeSpan.FromMinutes(2))
            .Build();

        await using var provider = services.BuildServiceProvider();
        var writer = provider.GetRequiredService<IWorkOrchestrator<Turn>>();
        var runner = provider.GetRequiredService<TurnRunner>();

        await Assert.That(await SubmitTurnAsync(writer, new Turn("contoso", "s1", "0"))).IsEqualTo("Your message is queued.");
        await runner.FirstStarted.Task.WaitAsync(WaitTimeout);

        for (var i = 1; i <= 8; i++)
        {
            await Assert.That(await SubmitTurnAsync(writer, new Turn("contoso", "s1", $"{i}"))).IsEqualTo("Your message is queued.");
        }

        await Assert.That(await SubmitTurnAsync(writer, new Turn("contoso", "s1", "9")))
            .IsEqualTo("This session already has messages waiting. Try again soon.");
        await Assert.That(await SubmitTurnAsync(writer, new Turn("contoso", "s2", "other"))).IsEqualTo("Your message is queued.");
        await runner.Finished("other").WaitAsync(WaitTimeout);

        runner.ReleaseFirst();
        await writer.DrainAsync().WaitAsync(WaitTimeout);

        await Assert.That(await SubmitTurnAsync(writer, new Turn("contoso", "s1", "late"))).IsEqualTo("The service is shutting down.");
        await Assert.That(runner.Order("s1")).IsEquivalentTo(Enumerable.Range(0, 9).Select(static i => $"{i}"), CollectionOrdering.Matching);
        await Assert.That(runner.MaxConcurrentPerSession).IsEqualTo(1);
    }

    /// <summary>The keyed example's reply for a full queue is the capacity message.</summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task KeyedDispatch_FullQueue_RepliesBusy()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkOrchestrator<Turn>(static options =>
            {
                options.Capacity = 1;
                options.WorkerCount = 1;
            })
            .WithKeyedDispatch(static turn => (turn.TenantId, turn.SessionId), maxPendingPerKey: 8, maxConcurrencyPerKey: 1)
            .WithHandler(static (_, ct) => new ValueTask(Task.Delay(Timeout.Infinite, ct)))
            .Build();

        await using var provider = services.BuildServiceProvider();
        var writer = provider.GetRequiredService<IWorkOrchestrator<Turn>>();

        await Assert.That(await SubmitTurnAsync(writer, new Turn("t", "a", "1"))).IsEqualTo("Your message is queued.");
        using var deadline = new CancellationTokenSource(WaitTimeout);
        while (writer.PendingCount != 0)
        {
            await Task.Delay(1, deadline.Token);
        }

        await Assert.That(await SubmitTurnAsync(writer, new Turn("t", "a", "2"))).IsEqualTo("Your message is queued.");
        await Assert.That(await SubmitTurnAsync(writer, new Turn("t", "b", "3"))).IsEqualTo("The service is busy. Try again later.");
        await writer.AbortAsync().WaitAsync(WaitTimeout);
    }

    /// <summary>
    /// The one-retry-owner example retries a classified transient failure with handler resilience
    /// and runs every attempt in a fresh scope.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Test]
    public async Task OneRetryOwner_RetriesTransientFailureInFreshScopes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<InvoiceGateway>();

        services.AddWorkOrchestrator<SendInvoice>()
            .WithHandler<SendInvoice, SendInvoiceHandler>(ServiceLifetime.Scoped)
            .WithHandlerResilience(options =>
            {
                options.AttemptTimeout = TimeSpan.FromSeconds(30);
                options.MaxRetries = 2;
                options.ShouldRetry = static ex => ex is HttpRequestException;
            })
            .WithDeadLetterQueue(options => options.MaxRetries = 0)
            .WithAdmissionResilience()
            .Build();

        await using var provider = services.BuildServiceProvider();
        var writer = provider.GetRequiredService<IWorkOrchestrator<SendInvoice>>();
        var gateway = provider.GetRequiredService<InvoiceGateway>();

        await Assert.That((await writer.EnqueueAsync(new SendInvoice(42))).IsAccepted).IsTrue();
        await gateway.Sent.Task.WaitAsync(WaitTimeout);
        await writer.DrainAsync().WaitAsync(WaitTimeout);

        await Assert.That(gateway.Attempts).IsEqualTo(3);
        await Assert.That(gateway.DistinctHandlers).IsEqualTo(3);
    }

    /// <summary>Submits a turn with the README's rejection handling and returns the reply.</summary>
    /// <param name="writer">The orchestrator.</param>
    /// <param name="turn">The turn.</param>
    /// <returns>The reply the README example chooses.</returns>
    private static async Task<string> SubmitTurnAsync(IWorkWriter<Turn> writer, Turn turn)
    {
        var result = await writer.EnqueueAsync(turn);
        var reply = result.Reason switch
        {
            null => "Your message is queued.",
            RejectionReason.KeyLimitExceeded => "This session already has messages waiting. Try again soon.",
            RejectionReason.CapacityExceeded => "The service is busy. Try again later.",
            _ => "The service is shutting down.",
        };
        return reply;
    }

    /// <summary>Collapses every whitespace run to one space.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The normalized text.</returns>
    private static string Normalize(string text) => Whitespace().Replace(text, " ").Trim();

    /// <summary>Finds the repository root by walking up from the test output folder.</summary>
    /// <returns>The repository root.</returns>
    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "README.md")) && Directory.Exists(Path.Combine(dir.FullName, "src", "Bifrost.Tests")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"```csharp\r?\n(?<code>.*?)```", RegexOptions.Singleline)]
    private static partial Regex CSharpBlock();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>The quick start work item.</summary>
    /// <param name="ImageId">The image to resize.</param>
    internal sealed record ResizeImage(Guid ImageId);

    /// <summary>The keyed example work item.</summary>
    /// <param name="TenantId">The tenant.</param>
    /// <param name="SessionId">The session.</param>
    /// <param name="Message">The message, used as the turn's name.</param>
    internal sealed record Turn(string TenantId, string SessionId, string Message);

    /// <summary>The retry example work item.</summary>
    /// <param name="InvoiceId">The invoice.</param>
    internal sealed record SendInvoice(int InvoiceId);

    /// <summary>Records the resized image.</summary>
    internal sealed class ResizeLog
    {
        /// <summary>Gets the signal completed with the first resized image.</summary>
        public TaskCompletionSource<Guid> Resized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>The quick start handler.</summary>
    /// <param name="log">The log.</param>
    internal sealed class ResizeImageHandler(ResizeLog log) : IWorkHandler<ResizeImage>
    {
        /// <inheritdoc/>
        public ValueTask HandleAsync(ResizeImage work, CancellationToken ct)
        {
            log.Resized.TrySetResult(work.ImageId);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Runs turns for the keyed example. The first turn waits until it is released.</summary>
    internal sealed class TurnRunner
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _finished = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _running = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<Turn> _order = new();
        private int _maxConcurrent;

        /// <summary>Gets the signal completed when the first turn starts.</summary>
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets the most turns of one session that ran at the same time.</summary>
        public int MaxConcurrentPerSession => Volatile.Read(ref _maxConcurrent);

        /// <summary>Releases the first turn.</summary>
        public void ReleaseFirst() => _release.TrySetResult();

        /// <summary>Gets a task that completes when the named turn finishes.</summary>
        /// <param name="message">The turn's message.</param>
        /// <returns>The task.</returns>
        public Task Finished(string message) => Signal(message).Task;

        /// <summary>Gets the messages of a session in completion order.</summary>
        /// <param name="sessionId">The session.</param>
        /// <returns>The messages.</returns>
        public IReadOnlyList<string> Order(string sessionId)
            => _order.Where(t => t.SessionId == sessionId).Select(static t => t.Message).ToList();

        /// <summary>Runs a turn.</summary>
        /// <param name="turn">The turn.</param>
        /// <param name="ct">The handler token.</param>
        /// <returns>A task that completes when the turn finishes.</returns>
        public async Task RunAsync(Turn turn, CancellationToken ct)
        {
            var running = _running.AddOrUpdate(turn.SessionId, 1, static (_, n) => n + 1);
            InterlockedMax(ref _maxConcurrent, running);
            if (turn.Message == "0")
            {
                FirstStarted.TrySetResult();
                await _release.Task.WaitAsync(ct);
            }

            _running.AddOrUpdate(turn.SessionId, 0, static (_, n) => n - 1);
            _order.Enqueue(turn);
            Signal(turn.Message).TrySetResult();
        }

        private static void InterlockedMax(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (value > current)
            {
                var seen = Interlocked.CompareExchange(ref target, value, current);
                if (seen == current)
                {
                    return;
                }

                current = seen;
            }
        }

        private TaskCompletionSource Signal(string message)
            => _finished.GetOrAdd(message, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    /// <summary>A gateway that fails the first two sends with a transient error.</summary>
    internal sealed class InvoiceGateway
    {
        private readonly ConcurrentDictionary<SendInvoiceHandler, byte> _handlers = new();
        private int _attempts;

        /// <summary>Gets the signal completed when a send succeeds.</summary>
        public TaskCompletionSource Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets the number of send attempts.</summary>
        public int Attempts => Volatile.Read(ref _attempts);

        /// <summary>Gets the number of distinct scoped handler instances that sent.</summary>
        public int DistinctHandlers => _handlers.Count;

        /// <summary>Sends an invoice.</summary>
        /// <param name="handler">The scoped handler instance that sends.</param>
        /// <exception cref="HttpRequestException">Thrown on the first two attempts.</exception>
        public void Send(SendInvoiceHandler handler)
        {
            _handlers.TryAdd(handler, 0);
            if (Interlocked.Increment(ref _attempts) <= 2)
            {
                throw new HttpRequestException("Transient gateway failure.");
            }

            Sent.TrySetResult();
        }
    }

    /// <summary>The retry example's scoped handler.</summary>
    /// <param name="gateway">The gateway.</param>
    internal sealed class SendInvoiceHandler(InvoiceGateway gateway) : IWorkHandler<SendInvoice>
    {
        /// <inheritdoc/>
        public ValueTask HandleAsync(SendInvoice work, CancellationToken ct)
        {
            gateway.Send(this);
            return ValueTask.CompletedTask;
        }
    }
}
