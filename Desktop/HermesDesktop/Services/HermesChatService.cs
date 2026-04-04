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

        // Agent.ChatAsync adds user msg + tool calls + assistant response to session
        var response = await _agent.ChatAsync(message, _currentSession, ct);

        // Persist all new messages added during this turn (user + tool calls + assistant)
        for (var i = messageCountBefore; i < _currentSession.Messages.Count; i++)
        {
            await _transcriptStore.SaveMessageAsync(_currentSession.Id, _currentSession.Messages[i], ct);
        }

        _logger.LogInformation("Chat reply for session {SessionId}: {Length} chars", _currentSession.Id, response.Length);
        return new HermesChatReply(response, _currentSession.Id);
    }

    // ── Stream (token-by-token) ──

    public async IAsyncEnumerable<string> StreamAsync(
        string message,
        [EnumeratorCancellation] CancellationToken ct)
    {
        EnsureSession();
        _streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Save user message
        var userMsg = new Message { Role = "user", Content = message };
        _currentSession!.AddMessage(userMsg);
        await _transcriptStore.SaveMessageAsync(_currentSession.Id, userMsg, _streamCts.Token);

        // Stream tokens
        var fullResponse = new System.Text.StringBuilder();
        await foreach (var token in _chatClient.StreamAsync(_currentSession.Messages, _streamCts.Token))
        {
            fullResponse.Append(token);
            yield return token;
        }

        // Save complete assistant response
        var assistantMsg = new Message { Role = "assistant", Content = fullResponse.ToString() };
        _currentSession.AddMessage(assistantMsg);
        await _transcriptStore.SaveMessageAsync(_currentSession.Id, assistantMsg, CancellationToken.None);
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
