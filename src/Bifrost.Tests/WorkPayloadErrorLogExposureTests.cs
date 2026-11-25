// =============================================================================
// <copyright file="WorkPayloadErrorLogExposureTests.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using System.Reflection;

using Bifrost.Core;
using Bifrost.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Bifrost.Tests;

/// <summary>
/// Handler failures must not write admitted work payloads into error logs.
/// </summary>
/// <remarks>
/// Each test admits a payload through a public enqueue operation, forces the handler
/// to throw, and reads the log record produced by Microsoft.Extensions.Logging.
/// The record fails the test while the work object or its field text is present.
/// </remarks>
[NotInParallel("WorkPayloadErrorLogCapture")]
public sealed class WorkPayloadErrorLogExposureTests
{
    private const string AccountTokenSecret = "PLACEHOLDER_ACCOUNT_TOKEN";
    private const string PaymentDetailSecret = "PLACEHOLDER_PAYMENT_DETAIL";
    private const string StringPayloadSecret = "PLACEHOLDER_STRING_PAYLOAD";

    /// <summary>Constructor workers log a record payload admitted by <see cref="IWorkWriter{TWork}.Enqueue"/>.</summary>
    [Test]
    [Arguments(DispatchStrategy.Fifo)]
    [Arguments(DispatchStrategy.PriorityExact)]
    [Arguments(DispatchStrategy.PriorityRelaxed)]
    public async Task Enqueue_ConstructorWorker_SimpleConsole_OmitsRecordFields(DispatchStrategy strategy)
    {
        var work = new SensitiveJob(AccountTokenSecret, PaymentDetailSecret);
        await AssertFailureOmitsPayload(
            work,
            static item => item.AccountToken == AccountTokenSecret,
            [AccountTokenSecret, PaymentDetailSecret],
            [typeof(SensitiveJob)],
            strategy,
            ConsoleFormatterNames.Simple,
            static (orchestrator, item) => new ValueTask<EnqueueResult>(orchestrator.Enqueue(item)),
            WorkerKind.Constructor).ConfigureAwait(false);
    }

    /// <summary><see cref="IWorkWriter{TWork}.TryEnqueue"/> admits the same payload into the failure log.</summary>
    [Test]
    public async Task TryEnqueue_ConstructorWorker_SimpleConsole_OmitsRecordFields()
    {
        var work = new SensitiveJob(AccountTokenSecret, PaymentDetailSecret);
        await AssertFailureOmitsPayload(
            work,
            static item => item.AccountToken == AccountTokenSecret,
            [AccountTokenSecret, PaymentDetailSecret],
            [typeof(SensitiveJob)],
            DispatchStrategy.Fifo,
            ConsoleFormatterNames.Simple,
            static (orchestrator, item) => new ValueTask<EnqueueResult>(
                orchestrator.TryEnqueue(item)
                    ? EnqueueResult.Accepted
                    : EnqueueResult.Rejected(RejectionReason.CapacityExceeded)),
            WorkerKind.Constructor).ConfigureAwait(false);
    }

    /// <summary><see cref="IWorkWriter{TWork}.EnqueueAsync"/> admits the same payload into the failure log.</summary>
    [Test]
    public async Task EnqueueAsync_ConstructorWorker_SimpleConsole_OmitsRecordFields()
    {
        var work = new SensitiveJob(AccountTokenSecret, PaymentDetailSecret);
        await AssertFailureOmitsPayload(
            work,
            static item => item.AccountToken == AccountTokenSecret,
            [AccountTokenSecret, PaymentDetailSecret],
            [typeof(SensitiveJob)],
            DispatchStrategy.Fifo,
            ConsoleFormatterNames.Simple,
            static (orchestrator, item) => orchestrator.EnqueueAsync(item),
            WorkerKind.Constructor).ConfigureAwait(false);
    }

    /// <summary>The FIFO wait path keeps the admitted object that the failure log later receives.</summary>
    [Test]
    public async Task EnqueueAsync_FifoQueueFullWait_SimpleConsole_OmitsRecordFields()
    {
        var secrets = new[] { AccountTokenSecret, PaymentDetailSecret };
        var handler = new GatedHandler();
        using var scope = await ConsoleLogScope.StartAsync(ConsoleFormatterNames.Simple).ConfigureAwait(false);
        await using var orchestrator = new WorkOrchestrator<SensitiveJob>(
            handler,
            Options.Create(new WorkOrchestratorOptions
            {
                Capacity = 1,
                WorkerCount = 1,
                FullMode = QueueFullMode.Wait,
                DispatchStrategy = DispatchStrategy.Fifo,
            }),
            scope.CreateLogger<SensitiveJob>());

        try
        {
            var blocker = orchestrator.Enqueue(new SensitiveJob("ordinary-account", "ordinary-payment"));
            await Assert.That(blocker.IsAccepted).IsTrue();
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var filler = orchestrator.Enqueue(new SensitiveJob("filler-account", "filler-payment"));
            await Assert.That(filler.IsAccepted).IsTrue();
            await Assert.That(orchestrator.PendingCount).IsEqualTo(1);

            var waiting = orchestrator.EnqueueAsync(new SensitiveJob(AccountTokenSecret, PaymentDetailSecret)).AsTask();
            await Task.Yield();
            await Assert.That(waiting.IsCompleted).IsFalse();
            handler.Release();
            var admitted = await waiting.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await Assert.That(admitted.IsAccepted).IsTrue();
            await handler.TargetSeen.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await scope.Capture.ErrorLogged.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            handler.Release();
            await orchestrator.DisposeAsync().ConfigureAwait(false);
        }

        await AssertSecretsAbsent(scope, secrets, typeof(SensitiveJob)).ConfigureAwait(false);
    }

    /// <summary>A retiring worker still formats the retained payload when the handler throws.</summary>
    [Test]
    [Arguments(DispatchStrategy.Fifo)]
    [Arguments(DispatchStrategy.PriorityExact)]
    [Arguments(DispatchStrategy.PriorityRelaxed)]
    public async Task Enqueue_RetiredWorker_SimpleConsole_OmitsRecordFields(DispatchStrategy strategy)
    {
        var work = new SensitiveJob(AccountTokenSecret, PaymentDetailSecret);
        await AssertFailureOmitsPayload(
            work,
            static item => item.AccountToken == AccountTokenSecret,
            [AccountTokenSecret, PaymentDetailSecret],
            [typeof(SensitiveJob)],
            strategy,
            ConsoleFormatterNames.Simple,
            static (orchestrator, item) => new ValueTask<EnqueueResult>(orchestrator.Enqueue(item)),
            WorkerKind.RetiredDrain).ConfigureAwait(false);
    }

    /// <summary>The dependency-injection builder without a dead-letter decorator uses the same failure log.</summary>
    [Test]
    public async Task Enqueue_DependencyInjectionBuilder_SimpleConsole_OmitsRecordFields()
    {
        var secrets = new[] { AccountTokenSecret, PaymentDetailSecret };
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scope = await ConsoleLogScope.StartAsync(ConsoleFormatterNames.Simple).ConfigureAwait(false);
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(scope.Factory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddWorkOrchestrator<SensitiveJob>(options =>
        {
            options.Capacity = 8;
            options.WorkerCount = 1;
            options.FullMode = QueueFullMode.Reject;
        }).WithHandler((SensitiveJob work, CancellationToken cancellationToken) =>
        {
            if (work.AccountToken == AccountTokenSecret)
            {
                seen.TrySetResult();
                throw new InvalidOperationException("handler failed");
            }

            return ValueTask.CompletedTask;
        }).Build();

        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<IWorkOrchestrator<SensitiveJob>>();
        try
        {
            var admitted = orchestrator.Enqueue(new SensitiveJob(AccountTokenSecret, PaymentDetailSecret));
            await Assert.That(admitted.IsAccepted).IsTrue();
            await seen.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await scope.Capture.ErrorLogged.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            await orchestrator.DisposeAsync().ConfigureAwait(false);
        }

        await AssertSecretsAbsent(scope, secrets, typeof(SensitiveJob)).ConfigureAwait(false);
    }

    /// <summary>The JSON console formatter writes the same work-item string conversion.</summary>
    [Test]
    public async Task Enqueue_ConstructorWorker_JsonConsole_OmitsRecordFields()
    {
        var work = new SensitiveJob(AccountTokenSecret, PaymentDetailSecret);
        await AssertFailureOmitsPayload(
            work,
            static item => item.AccountToken == AccountTokenSecret,
            [AccountTokenSecret, PaymentDetailSecret],
            [typeof(SensitiveJob)],
            DispatchStrategy.Fifo,
            ConsoleFormatterNames.Json,
            static (orchestrator, item) => new ValueTask<EnqueueResult>(orchestrator.Enqueue(item)),
            WorkerKind.Constructor).ConfigureAwait(false);
    }

    /// <summary>A string work item is the payload, so its text must stay out of the failure log.</summary>
    [Test]
    public async Task Enqueue_ConstructorWorker_SimpleConsole_OmitsStringPayload()
    {
        await AssertFailureOmitsPayload(
            StringPayloadSecret,
            static item => item == StringPayloadSecret,
            [StringPayloadSecret],
            [],
            DispatchStrategy.Fifo,
            ConsoleFormatterNames.Simple,
            static (orchestrator, item) => new ValueTask<EnqueueResult>(orchestrator.Enqueue(item)),
            WorkerKind.Constructor).ConfigureAwait(false);
    }

    /// <summary>A class payload must not be passed to the log formatter, even when its default string conversion hides fields.</summary>
    [Test]
    public async Task Enqueue_ConstructorWorker_SimpleConsole_OmitsClassInstance()
    {
        var work = new PaymentJob(AccountTokenSecret, PaymentDetailSecret);
        await AssertFailureOmitsPayload(
            work,
            static item => item.AccountToken == AccountTokenSecret,
            [AccountTokenSecret, PaymentDetailSecret],
            [typeof(PaymentJob)],
            DispatchStrategy.Fifo,
            ConsoleFormatterNames.Simple,
            static (orchestrator, item) => new ValueTask<EnqueueResult>(orchestrator.Enqueue(item)),
            WorkerKind.Constructor).ConfigureAwait(false);
    }

    /// <summary>A collection work item is expanded by the log formatter, so element text must stay out of the record.</summary>
    [Test]
    public async Task Enqueue_ConstructorWorker_SimpleConsole_OmitsCollectionElements()
    {
        string[] work = [AccountTokenSecret, PaymentDetailSecret];
        await AssertFailureOmitsPayload(
            work,
            static item => item.Length == 2 && item[0] == AccountTokenSecret,
            [AccountTokenSecret, PaymentDetailSecret],
            [typeof(string[])],
            DispatchStrategy.Fifo,
            ConsoleFormatterNames.Simple,
            static (orchestrator, item) => new ValueTask<EnqueueResult>(orchestrator.Enqueue(item)),
            WorkerKind.Constructor).ConfigureAwait(false);
    }

    private static async Task AssertFailureOmitsPayload<TWork>(
        TWork work,
        Func<TWork, bool> isTarget,
        string[] secrets,
        Type[] forbiddenValueTypes,
        DispatchStrategy strategy,
        string formatterName,
        Func<WorkOrchestrator<TWork>, TWork, ValueTask<EnqueueResult>> admit,
        WorkerKind workerKind)
    {
        var handler = new TargetHandler<TWork>(isTarget);
        using var scope = await ConsoleLogScope.StartAsync(formatterName).ConfigureAwait(false);
        await using var orchestrator = new WorkOrchestrator<TWork>(
            handler,
            Options.Create(new WorkOrchestratorOptions
            {
                Capacity = 8,
                WorkerCount = workerKind == WorkerKind.RetiredDrain ? 0 : 1,
                FullMode = QueueFullMode.Reject,
                DispatchStrategy = strategy,
            }),
            scope.CreateLogger<TWork>());

        try
        {
            var admitted = await admit(orchestrator, work).ConfigureAwait(false);
            await Assert.That(admitted.IsAccepted).IsTrue();
            if (workerKind == WorkerKind.RetiredDrain)
            {
                using var retirement = new CancellationTokenSource();
                await retirement.CancelAsync().ConfigureAwait(false);
                var worker = orchestrator.CreateWorkerFunction(stateCallback: null, retirement.Token);
                var running = worker("retired-worker", CancellationToken.None);
                await handler.TargetSeen.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await scope.Capture.ErrorLogged.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await running.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            else
            {
                await handler.TargetSeen.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await scope.Capture.ErrorLogged.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        finally
        {
            await orchestrator.DisposeAsync().ConfigureAwait(false);
        }

        await AssertSecretsAbsent(scope, secrets, forbiddenValueTypes).ConfigureAwait(false);
    }

    private static async Task AssertSecretsAbsent(ConsoleLogScope scope, IReadOnlyList<string> secrets, params Type[] forbiddenValueTypes)
    {
        var logs = scope.Capture.Snapshot();
        await Assert.That(logs.Any(entry => entry.Level == LogLevel.Error)).IsTrue();
        var console = scope.FlushConsole();
        foreach (var secret in secrets)
        {
            await Assert.That(console.Contains(secret, StringComparison.Ordinal)).IsFalse();
        }

        foreach (var entry in logs)
        {
            foreach (var secret in secrets)
            {
                await Assert.That(entry.Message.Contains(secret, StringComparison.Ordinal)).IsFalse();
                await Assert.That(entry.ExceptionText.Contains(secret, StringComparison.Ordinal)).IsFalse();
            }

            foreach (var value in entry.Values)
            {
                var printed = value?.ToString() ?? string.Empty;
                foreach (var secret in secrets)
                {
                    await Assert.That(printed.Contains(secret, StringComparison.Ordinal)).IsFalse();
                }

                if (value is not null)
                {
                    foreach (var forbidden in forbiddenValueTypes)
                    {
                        await Assert.That(value.GetType()).IsNotEqualTo(forbidden);
                    }
                }
            }
        }
    }

    private enum WorkerKind
    {
        Constructor,
        RetiredDrain,
    }

    private sealed record SensitiveJob(string AccountToken, string PaymentDetail);

    private sealed class PaymentJob(string accountToken, string paymentDetail)
    {
        public string AccountToken { get; } = accountToken;

        public string PaymentDetail { get; } = paymentDetail;
    }

    private sealed class TargetHandler<TWork>(Func<TWork, bool> isTarget) : IWorkHandler<TWork>
    {
        public TaskCompletionSource TargetSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask HandleAsync(TWork work, CancellationToken cancellationToken)
        {
            if (!isTarget(work))
            {
                return ValueTask.CompletedTask;
            }

            TargetSeen.TrySetResult();
            throw new InvalidOperationException("handler failed");
        }
    }

    private sealed class GatedHandler : IWorkHandler<SensitiveJob>
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource TargetSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public async ValueTask HandleAsync(SensitiveJob work, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                Entered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (work.AccountToken == AccountTokenSecret)
            {
                TargetSeen.TrySetResult();
                throw new InvalidOperationException("handler failed");
            }
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly CapturingLogger _logger = new();

        public Task ErrorLogged => _logger.ErrorLogged;

        public ILogger CreateLogger(string categoryName) => _logger;

        public CapturedLog[] Snapshot() => _logger.Snapshot();

        public void Dispose()
        {
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly Lock _gate = new();
            private readonly List<CapturedLog> _entries = [];
            private readonly TaskCompletionSource _errorLogged = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task ErrorLogged => _errorLogged.Task;

            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull
                => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var values = new List<object?>();
                if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
                {
                    foreach (var pair in pairs)
                    {
                        if (pair.Key == "{OriginalFormat}")
                        {
                            continue;
                        }

                        values.Add(pair.Value);
                    }
                }

                var captured = new CapturedLog(
                    logLevel,
                    formatter(state, exception),
                    values.ToArray(),
                    exception?.ToString() ?? string.Empty);
                lock (_gate)
                {
                    _entries.Add(captured);
                }

                if (logLevel == LogLevel.Error)
                {
                    _errorLogged.TrySetResult();
                }
            }

            public CapturedLog[] Snapshot()
            {
                lock (_gate)
                {
                    return _entries.ToArray();
                }
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed record CapturedLog(LogLevel Level, string Message, object?[] Values, string ExceptionText);

    private sealed class ConsoleLogScope : IDisposable
    {
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private readonly ServiceProvider _services;
        private readonly StringWriter _buffer = new();
        private int _disposed;
        private int _flushed;

        private ConsoleLogScope(ServiceProvider services)
        {
            _services = services;
            Factory = services.GetRequiredService<ILoggerFactory>();
            RedirectConsoleWriters(services, _buffer);
        }

        public CapturingLoggerProvider Capture { get; private init; } = null!;

        public ILoggerFactory Factory { get; }

        public static async Task<ConsoleLogScope> StartAsync(string formatterName)
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            var capture = new CapturingLoggerProvider();
            try
            {
                var services = new ServiceCollection();
                services.AddLogging(builder =>
                {
                    builder.ClearProviders();
                    builder.SetMinimumLevel(LogLevel.Trace);
                    builder.AddConsole(options => options.FormatterName = formatterName);
                    builder.AddProvider(capture);
                });
                var provider = services.BuildServiceProvider();
                return new ConsoleLogScope(provider) { Capture = capture };
            }
            catch
            {
                Gate.Release();
                throw;
            }
        }

        public ILogger<WorkOrchestrator<TWork>> CreateLogger<TWork>() =>
            Factory.CreateLogger<WorkOrchestrator<TWork>>();

        public string FlushConsole()
        {
            DisposeServices();
            return _buffer.ToString();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            DisposeServices();
            Gate.Release();
        }

        private void DisposeServices()
        {
            if (Interlocked.Exchange(ref _flushed, 1) == 0)
            {
                _services.Dispose();
            }
        }

        private static void RedirectConsoleWriters(ServiceProvider services, StringWriter buffer)
        {
            foreach (var provider in services.GetServices<ILoggerProvider>())
            {
                if (provider is not ConsoleLoggerProvider)
                {
                    continue;
                }

                var processor = provider.GetType()
                    .GetField("_messageQueue", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(provider)!;
                RedirectWriter(processor, buffer);
            }
        }

        private static void RedirectWriter(object processor, StringWriter buffer)
        {
            foreach (var propertyName in new[] { "Console", "ErrorConsole" })
            {
                var console = processor.GetType().GetProperty(propertyName)!.GetValue(processor)!;
                var writer = console.GetType().GetField("_textWriter", BindingFlags.Instance | BindingFlags.NonPublic)!;
                writer.SetValue(console, buffer);
            }
        }
    }
}
