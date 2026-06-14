// =============================================================================
// <copyright file="CapturingLogger.cs" company="Levelup Software">
// Copyright (c) Levelup Software. All rights reserved.
// </copyright>
// =============================================================================

using Microsoft.Extensions.Logging;

namespace Bifrost.Tests.Scheduling.TickEngine;

/// <summary>
/// A test <see cref="ILogger{TCategoryName}"/> that records every log entry's level
/// and rendered message for assertion. The project does not reference
/// <c>Microsoft.Extensions.Logging.Testing</c>, so this stands in for
/// <c>FakeLogger</c>. Recording is lock-guarded so entries logged from the loop's
/// own thread and from pool-thread continuations are observed without a data race.
/// </summary>
/// <typeparam name="T">The logger category type.</typeparam>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly object gate = new();
    private readonly List<LogEntry> entries = [];

    /// <summary>
    /// A single captured log entry: its level and rendered message.
    /// </summary>
    /// <param name="Level">The log level.</param>
    /// <param name="Message">The rendered message.</param>
    public readonly record struct LogEntry(LogLevel Level, string Message);

    /// <summary>
    /// Gets a snapshot of the entries captured so far, oldest first.
    /// </summary>
    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (this.gate)
            {
                return [.. this.entries];
            }
        }
    }

    /// <inheritdoc/>
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc/>
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        lock (this.gate)
        {
            this.entries.Add(new LogEntry(logLevel, message));
        }
    }

    /// <summary>
    /// Returns whether any entry at the given level was captured.
    /// </summary>
    /// <param name="level">The level to look for.</param>
    /// <returns><see langword="true"/> if at least one entry at that level exists.</returns>
    public bool Any(LogLevel level)
    {
        lock (this.gate)
        {
            return this.entries.Exists(e => e.Level == level);
        }
    }

    /// <summary>
    /// Returns whether any entry at the given level contains the given substring.
    /// </summary>
    /// <param name="level">The level to look for.</param>
    /// <param name="substring">The substring the message must contain.</param>
    /// <returns><see langword="true"/> if a matching entry exists.</returns>
    public bool Contains(LogLevel level, string substring)
    {
        lock (this.gate)
        {
            return this.entries.Exists(
                e => e.Level == level && e.Message.Contains(substring, StringComparison.Ordinal));
        }
    }
}
