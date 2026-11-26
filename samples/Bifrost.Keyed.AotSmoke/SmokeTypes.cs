using System.Collections.Concurrent;

using Bifrost.Core;

using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.Keyed.AotSmoke;

/// <summary>A smoke work item.</summary>
/// <param name="Key">The work key.</param>
/// <param name="Sequence">The admission number within the key.</param>
/// <param name="Block">Whether the handler waits on the shared gate.</param>
internal sealed record SmokeTurn(string Key, int Sequence, bool Block = false)
{
    /// <summary>Gets the display name, for example <c>A1</c>.</summary>
    public string Name => Key.Length == 1 ? $"{Key}{Sequence}" : Key;
}

/// <summary>A scoped service that counts how many attempt scopes were created and disposed.</summary>
internal sealed class ScopeProbe : IDisposable
{
    private readonly SmokeState _state;

    /// <summary>Initializes a new instance of the <see cref="ScopeProbe"/> class.</summary>
    /// <param name="state">The shared smoke state.</param>
    public ScopeProbe(SmokeState state)
    {
        _state = state;
        _state.OnScopeCreated();
    }

    /// <inheritdoc/>
    public void Dispose() => _state.OnScopeDisposed();
}

/// <summary>The shared, AOT-safe state the handler records into and the program checks.</summary>
internal sealed class SmokeState
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _completed = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<SmokeTurn> _order = new();
    private int _scopesCreated;
    private int _scopesDisposed;

    /// <summary>Gets the number of attempt scopes created.</summary>
    public int ScopesCreated => Volatile.Read(ref _scopesCreated);

    /// <summary>Gets the number of attempt scopes disposed.</summary>
    public int ScopesDisposed => Volatile.Read(ref _scopesDisposed);

    /// <summary>Gets the number of items that ran.</summary>
    public int TotalCompleted => _order.Count;

    /// <summary>Gets the gate that blocking handlers wait on.</summary>
    public Task Gate => _gate.Task;

    /// <summary>Opens the gate.</summary>
    public void OpenGate() => _gate.TrySetResult();

    /// <summary>Gets a task that completes when the named item starts.</summary>
    /// <param name="name">The item name.</param>
    /// <returns>The task.</returns>
    public Task Started(string name) => Signal(_started, name).Task;

    /// <summary>Gets a task that completes when the named item finishes.</summary>
    /// <param name="name">The item name.</param>
    /// <returns>The task.</returns>
    public Task Completed(string name) => Signal(_completed, name).Task;

    /// <summary>Gets the sequence numbers of a key in completion order.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The sequence numbers.</returns>
    public IReadOnlyList<int> Order(string key) => _order.Where(t => t.Key == key).Select(static t => t.Sequence).ToList();

    /// <summary>Records that an item started.</summary>
    /// <param name="turn">The item.</param>
    public void OnStarted(SmokeTurn turn) => Signal(_started, turn.Name).TrySetResult();

    /// <summary>Records that an item finished.</summary>
    /// <param name="turn">The item.</param>
    public void OnCompleted(SmokeTurn turn)
    {
        _order.Enqueue(turn);
        Signal(_completed, turn.Name).TrySetResult();
    }

    /// <summary>Counts a created scope.</summary>
    public void OnScopeCreated() => Interlocked.Increment(ref _scopesCreated);

    /// <summary>Counts a disposed scope.</summary>
    public void OnScopeDisposed() => Interlocked.Increment(ref _scopesDisposed);

    private static TaskCompletionSource Signal(ConcurrentDictionary<string, TaskCompletionSource> signals, string name)
        => signals.GetOrAdd(name, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
}

/// <summary>The scoped smoke handler.</summary>
internal static class SmokeHandler
{
    /// <summary>Records the item, waits on the gate when asked, and resolves a scoped service.</summary>
    /// <param name="services">The attempt's scoped service provider.</param>
    /// <param name="turn">The item.</param>
    /// <param name="ct">The handler token.</param>
    /// <returns>A task that completes when the item is recorded.</returns>
    public static async ValueTask HandleAsync(IServiceProvider services, SmokeTurn turn, CancellationToken ct)
    {
        _ = services.GetRequiredService<ScopeProbe>();
        var state = services.GetRequiredService<SmokeState>();
        state.OnStarted(turn);
        if (turn.Block)
        {
            await state.Gate.WaitAsync(ct);
        }

        state.OnCompleted(turn);
    }
}

/// <summary>Collects check results and prints them.</summary>
internal sealed class SmokeChecks
{
    private readonly List<string> _failures = [];

    /// <summary>Records a check.</summary>
    /// <param name="condition">Whether the check passed.</param>
    /// <param name="description">What the check verifies.</param>
    public void Expect(bool condition, string description)
    {
        Console.WriteLine($"{(condition ? "ok  " : "FAIL")} {description}");
        if (!condition)
        {
            _failures.Add(description);
        }
    }

    /// <summary>Records a check that an admission was rejected with a reason.</summary>
    /// <param name="result">The admission result.</param>
    /// <param name="reason">The expected reason.</param>
    /// <param name="name">The item name.</param>
    public void ExpectReason(EnqueueResult result, RejectionReason reason, string name)
        => Expect(result.Reason == reason, $"{name} rejected with {reason} (was {(result.IsAccepted ? "accepted" : result.Reason)})");

    /// <summary>Prints the verdict.</summary>
    /// <returns>The process exit code.</returns>
    public int Finish()
    {
        Console.WriteLine(_failures.Count == 0
            ? "Bifrost.Keyed AOT smoke — PASS."
            : $"Bifrost.Keyed AOT smoke — FAIL ({_failures.Count} check(s)).");
        return _failures.Count == 0 ? 0 : 1;
    }
}
