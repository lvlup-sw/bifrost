using System.Text.Json;

using Bifrost.Core;

namespace Bifrost.AgentFramework.Sample;

/// <summary>The state of a saved turn request.</summary>
internal enum TurnStatus
{
    /// <summary>Saved and accepted by the orchestrator, not finished yet.</summary>
    Pending,

    /// <summary>The orchestrator rejected the turn at admission.</summary>
    Rejected,

    /// <summary>The agent replied and the session update was saved.</summary>
    Completed,
}

/// <summary>A saved turn request and its result.</summary>
internal sealed class TurnRecord
{
    /// <summary>Gets the session the turn belongs to.</summary>
    public required SessionKey Session { get; init; }

    /// <summary>Gets the 1-based submission number of the turn within its session.</summary>
    public required int Sequence { get; init; }

    /// <summary>Gets the user message.</summary>
    public required string Message { get; init; }

    /// <summary>Gets or sets the turn state.</summary>
    public TurnStatus Status { get; set; }

    /// <summary>Gets or sets the agent reply.</summary>
    public string? Reply { get; set; }

    /// <summary>Gets or sets the admission rejection reason.</summary>
    public RejectionReason? Reason { get; set; }
}

/// <summary>
/// An in-memory stand-in for the app's database: turn requests, serialized agent sessions, and
/// results. A real app keeps these in durable storage.
/// </summary>
/// <remarks>
/// <see cref="CheckOut"/> refuses a session that another turn has checked out and not saved. With
/// keyed dispatch and <c>maxConcurrencyPerKey: 1</c> that never happens, because Bifrost does not
/// start the next turn of a session until the previous turn's handler and scope have exited.
/// </remarks>
internal sealed class SessionStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, TurnRecord> _turns = [];
    private readonly Dictionary<SessionKey, SessionRecord> _sessions = [];
    private int _checkedOut;
    private int _peakCheckedOut;

    /// <summary>Gets the most sessions that were checked out at the same time.</summary>
    public int PeakConcurrentSessions
    {
        get
        {
            lock (_gate)
            {
                return _peakCheckedOut;
            }
        }
    }

    /// <summary>Saves a new turn request before it is submitted to the orchestrator.</summary>
    /// <param name="session">The session.</param>
    /// <param name="message">The user message.</param>
    /// <returns>The work item that references the saved turn.</returns>
    public AgentTurn SaveRequest(SessionKey session, string message)
    {
        lock (_gate)
        {
            var record = GetSession(session);
            var turnId = Guid.NewGuid();
            _turns[turnId] = new TurnRecord { Session = session, Sequence = ++record.Submitted, Message = message };
            return new AgentTurn(session.TenantId, session.SessionId, turnId);
        }
    }

    /// <summary>Records that the orchestrator rejected a turn.</summary>
    /// <param name="turnId">The turn.</param>
    /// <param name="reason">The rejection reason.</param>
    public void MarkRejected(Guid turnId, RejectionReason reason)
    {
        lock (_gate)
        {
            var turn = _turns[turnId];
            turn.Status = TurnStatus.Rejected;
            turn.Reason = reason;
        }
    }

    /// <summary>Loads a saved turn request.</summary>
    /// <param name="turnId">The turn.</param>
    /// <returns>The turn record.</returns>
    public TurnRecord LoadRequest(Guid turnId)
    {
        lock (_gate)
        {
            return _turns[turnId];
        }
    }

    /// <summary>Checks out a session for one turn and returns its saved state.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The serialized agent session, or <see langword="null"/> for a new session.</returns>
    /// <exception cref="InvalidOperationException">Thrown when another turn has the session checked out.</exception>
    public JsonElement? CheckOut(SessionKey session)
    {
        lock (_gate)
        {
            var record = GetSession(session);
            if (record.CheckedOut)
            {
                throw new InvalidOperationException($"Session {session} is already running a turn.");
            }

            record.CheckedOut = true;
            _peakCheckedOut = Math.Max(_peakCheckedOut, ++_checkedOut);
            return record.State;
        }
    }

    /// <summary>Saves the session update and the reply of a turn, and releases the session.</summary>
    /// <param name="turnId">The turn.</param>
    /// <param name="state">The serialized agent session after the turn.</param>
    /// <param name="reply">The agent reply.</param>
    public void SaveResult(Guid turnId, JsonElement state, string reply)
    {
        lock (_gate)
        {
            var turn = _turns[turnId];
            var record = GetSession(turn.Session);
            record.State = state.Clone();
            record.Completed.Add(turnId);
            turn.Reply = reply;
            turn.Status = TurnStatus.Completed;
            Release(record);
        }
    }

    /// <summary>Releases a checked-out session without saving, for a turn that failed.</summary>
    /// <param name="session">The session.</param>
    public void Abandon(SessionKey session)
    {
        lock (_gate)
        {
            Release(GetSession(session));
        }
    }

    /// <summary>Gets each session's turns in completion order, followed by its rejected turns.</summary>
    /// <returns>The sessions and their turns.</returns>
    public IReadOnlyList<(SessionKey Session, IReadOnlyList<TurnRecord> Completed, IReadOnlyList<TurnRecord> Other)> Snapshot()
    {
        lock (_gate)
        {
            return _sessions
                .Select(pair => (
                    pair.Key,
                    (IReadOnlyList<TurnRecord>)pair.Value.Completed.Select(id => _turns[id]).ToList(),
                    (IReadOnlyList<TurnRecord>)_turns.Values
                        .Where(t => t.Session == pair.Key && t.Status != TurnStatus.Completed)
                        .OrderBy(t => t.Sequence)
                        .ToList()))
                .ToList();
        }
    }

    private SessionRecord GetSession(SessionKey session)
    {
        if (!_sessions.TryGetValue(session, out var record))
        {
            record = new SessionRecord();
            _sessions[session] = record;
        }

        return record;
    }

    private void Release(SessionRecord record)
    {
        if (record.CheckedOut)
        {
            record.CheckedOut = false;
            _checkedOut--;
        }
    }

    private sealed class SessionRecord
    {
        public JsonElement? State { get; set; }

        public bool CheckedOut { get; set; }

        public int Submitted { get; set; }

        public List<Guid> Completed { get; } = [];
    }
}
