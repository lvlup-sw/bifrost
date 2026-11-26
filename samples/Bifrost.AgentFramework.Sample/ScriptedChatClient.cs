using System.Runtime.CompilerServices;

using Microsoft.Extensions.AI;

namespace Bifrost.AgentFramework.Sample;

/// <summary>
/// A local, offline <see cref="IChatClient"/> that stands in for a model deployment so the sample
/// builds and runs without credentials or network access.
/// </summary>
/// <remarks>
/// Each reply starts with the number of user messages the client received, for example
/// <c>[3]</c> on the third turn of a session. The number is only right when the agent session,
/// and with it the chat history, was saved after each turn and loaded before the next one, so
/// the sample uses it to check that turns of one session ran one at a time and in order. The
/// delay makes each turn take long enough for a burst of turns to queue up behind it.
/// </remarks>
/// <param name="latency">How long each simulated model call takes.</param>
internal sealed class ScriptedChatClient(TimeSpan latency) : IChatClient
{
    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(latency, cancellationToken);
        var userMessages = messages.Where(static m => m.Role == ChatRole.User).ToList();
        var lastText = userMessages.Count == 0 ? string.Empty : userMessages[^1].Text;
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, $"[{userMessages.Count}] Noted: {lastText}"));
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}
