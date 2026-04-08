using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Hermes.Agent.Permissions;
using Hermes.Agent.Transcript;
using Microsoft.Extensions.Logging;

namespace HermesDesktop.Services;

/// <summary>
/// Pure C# chat service — bridges the WinUI frontend to the Hermes Agent core.
/// No Python sidecar. Direct in-process agent execution.
/// </summary>
internal sealed class HermesChatService : IDisposable
{
    private readonly Agent _agent;
    private readonly IChatClient _chatClient;
    private readonly TranscriptStore _transcriptStore;
    private readonly ILogger<HermesChatService> _logger;

    private Session? _currentSession;
    private CancellationTokenSource? _streamCts;
    private bool _disposed;

    public HermesChatService(
        Agent agent,
        IChatClient chatClient,
        TranscriptStore transcriptStore,
        ILogger<HermesChatService> logger)
    {
        _agent = agent;
        _chatClient = chatClient;
        _transcriptStore = transcriptStore;
        _logger = logger;
    }

    public string? CurrentSessionId => _currentSession?.Id;
    public Session? CurrentSession => _currentSession;
    public PermissionMode CurrentPermissionMode { get; private set; } = PermissionMode.Default;

    // ── Health Check ──

    public async Task<(bool IsHealthy, string Detail)> CheckHealthAsync(CancellationToken ct)
    {
        try
        {
            var messages = new[] { new Message { Role = "user", Content = "Respond with only: OK" } };
            var response = await _chatClient.CompleteAsync(messages, ct);
            return !string.IsNullOrEmpty(response)
                ? (true, "Connected to LLM")
                : (false, "Empty response from LLM");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ── Send (blocking, full response) ──

    public async Task<HermesChatReply> SendAsync(string message, CancellationToken ct)
    {
        EnsureSession();
        var messageCountBefore = _currentSession!.Messages.Count;

        try
        {
            var response = await _agent.ChatAsync(message, _currentSession, ct);

            // Persist all new messages (user + tool calls + assistant)
            await PersistNewMessagesAsync(messageCountBefore);

            _logger.LogInformation("Chat reply for session {SessionId}: {Length} chars", _currentSession.Id, response.Length);
            return new HermesChatReply(response, _currentSession.Id);
        }
        catch
        {
            // Persist whatever was added before the failure (at minimum the user message)
            await PersistNewMessagesAsync(messageCountBefore);
            throw;
        }
    }

    private async Task PersistNewMessagesAsync(int fromIndex)
    {
        for (var i = fromIndex; i < _currentSession!.Messages.Count; i++)
        {
            await _transcriptStore.SaveMessageAsync(_currentSession.Id, _currentSession.Messages[i], CancellationToken.None);
        }
    }

    // ── Stream (structured events: tokens + thinking) ──

    public async IAsyncEnumerable<ChatStreamEvent> StreamStructuredAsync(
        string message,
        [EnumeratorCancellation] CancellationToken ct)
    {
        EnsureSession();
        _streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Save user message
        var userMsg = new Message { Role = "user", Content = message };
        _currentSession!.AddMessage(userMsg);
        await _transcriptStore.SaveMessageAsync(_currentSession.Id, userMsg, _streamCts.Token);

        // Stream structured events
        var fullResponse = new System.Text.StringBuilder();
        await foreach (var evt in _chatClient.StreamAsync(null, _currentSession.Messages, null, _streamCts.Token))
        {
            switch (evt)
            {
                case Hermes.Agent.LLM.StreamEvent.TokenDelta td:
                    fullResponse.Append(td.Text);
                    yield return new ChatStreamEvent(ChatStreamEventType.Token, td.Text);
                    break;

                case Hermes.Agent.LLM.StreamEvent.ThinkingDelta tk:
                    yield return new ChatStreamEvent(ChatStreamEventType.Thinking, tk.Text);
                    break;

                case Hermes.Agent.LLM.StreamEvent.StreamError err:
                    yield return new ChatStreamEvent(ChatStreamEventType.Error, err.Error.Message);
                    break;
            }
        }

        // Save complete assistant response — skip if empty to avoid corrupting history
        if (fullResponse.Length > 0)
        {
            var assistantMsg = new Message { Role = "assistant", Content = fullResponse.ToString() };
            _currentSession.AddMessage(assistantMsg);
            await _transcriptStore.SaveMessageAsync(_currentSession.Id, assistantMsg, CancellationToken.None);
        }
    }

    // ── Legacy string streaming (kept for backwards compatibility) ──

    public async IAsyncEnumerable<string> StreamAsync(
        string message,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in StreamStructuredAsync(message, ct))
        {
            if (evt.Type == ChatStreamEventType.Token)
                yield return evt.Text;
        }
    }

    // ── Cancel ──

    public void CancelStream()
    {
        _streamCts?.Cancel();
        _logger.LogInformation("Stream cancelled for session {SessionId}", _currentSession?.Id);
    }

    // ── Session Management ──

    public void EnsureSession()
    {
        if (_currentSession is not null) return;
        _currentSession = new Session
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Platform = "desktop"
        };
        _logger.LogInformation("Created new session {SessionId}", _currentSession.Id);
    }

    public async Task LoadSessionAsync(string sessionId, CancellationToken ct)
    {
        var messages = await _transcriptStore.LoadSessionAsync(sessionId, ct);
        _currentSession = new Session
        {
            Id = sessionId,
            Platform = "desktop"
        };
        foreach (var msg in messages)
            _currentSession.AddMessage(msg);

        _logger.LogInformation("Loaded session {SessionId} with {Count} messages", sessionId, messages.Count);
    }

    public void ResetConversation()
    {
        _currentSession = null;
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
    }

    // ── Permission Mode ──

    public void SetPermissionMode(PermissionMode mode)
    {
        CurrentPermissionMode = mode;
    }

    // ── Tool Registration ──

    public void RegisterTool(ITool tool) => _agent.RegisterTool(tool);

    // ── Dispose ──

    public void Dispose()
    {
        if (_disposed) return;
        _streamCts?.Dispose();
        _disposed = true;
    }

    internal sealed record HermesChatReply(string Response, string SessionId);
}

// ── Structured stream events for UI consumption ──

internal enum ChatStreamEventType
{
    Token,
    Thinking,
    Error
}

internal sealed record ChatStreamEvent(ChatStreamEventType Type, string Text);
