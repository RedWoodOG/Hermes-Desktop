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
    private readonly TimelineStore? _timelineStore;
    private readonly PermissionManager _permissionManager;
    private readonly WorkspacePermissionRuleStore _permissionRuleStore;
    private readonly ILogger<HermesChatService> _logger;

    private Session? _currentSession;
    private CancellationTokenSource? _streamCts;
    private bool _disposed;

    public HermesChatService(
        Agent agent,
        IChatClient chatClient,
        TranscriptStore transcriptStore,
        PermissionManager permissionManager,
        WorkspacePermissionRuleStore permissionRuleStore,
        ILogger<HermesChatService> logger,
        TimelineStore? timelineStore = null)
    {
        _agent = agent;
        _chatClient = chatClient;
        _transcriptStore = transcriptStore;
        _timelineStore = timelineStore;
        _permissionManager = permissionManager;
        _permissionRuleStore = permissionRuleStore;
        _logger = logger;
        CurrentPermissionMode = _permissionManager.Mode;
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
        var turn = await StartTimelineTurnAsync(message, ct);

        try
        {
            var response = await _agent.ChatAsync(message, _currentSession, ct);

            // Persist all new messages (user + tool calls + assistant)
            await PersistNewMessagesAsync(messageCountBefore);
            await CompleteTimelineTurnAsync(turn, response, TurnStatus.Completed, null);

            _logger.LogInformation("Chat reply for session {SessionId}: {Length} chars", _currentSession.Id, response.Length);
            return new HermesChatReply(response, _currentSession.Id);
        }
        catch (OperationCanceledException)
        {
            await PersistNewMessagesAsync(messageCountBefore);
            await CompleteTimelineTurnAsync(turn, null, TurnStatus.Canceled, null);
            throw;
        }
        catch (Exception ex)
        {
            // Persist whatever was added before the failure (at minimum the user message)
            await PersistNewMessagesAsync(messageCountBefore);
            await CompleteTimelineTurnAsync(turn, null, TurnStatus.Failed, ex.Message);
            _logger.LogWarning(ex, "Chat send failed for session {SessionId}", _currentSession.Id);
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

    public async IAsyncEnumerable<ChatRuntimeEvent> StreamRuntimeAsync(
        string message,
        [EnumeratorCancellation] CancellationToken ct)
    {
        EnsureSession();
        _streamCts?.Dispose();
        _streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var fullResponse = new System.Text.StringBuilder();
        var turn = await StartTimelineTurnAsync(message, ct);
        var turnStatus = TurnStatus.Completed;
        string? turnError = null;
        await using var stream = _agent.StreamChatAsync(message, _currentSession!, _streamCts.Token)
            .GetAsyncEnumerator(_streamCts.Token);
        try
        {
            while (true)
            {
                Hermes.Agent.LLM.StreamEvent evt;
                try
                {
                    if (!await stream.MoveNextAsync())
                        break;

                    evt = stream.Current;
                }
                catch (OperationCanceledException)
                {
                    turnStatus = TurnStatus.Canceled;
                    throw;
                }
                catch (Exception ex)
                {
                    turnStatus = TurnStatus.Failed;
                    turnError = ex.Message;
                    throw;
                }

                switch (evt)
                {
                    case Hermes.Agent.LLM.StreamEvent.TokenDelta td:
                        // Tool-calling status messages (e.g. "[Calling tool: bash]") are
                        // informational — show in UI but don't accumulate into the saved response
                        if (td.Text.StartsWith("\n[Calling tool:") && td.Text.TrimEnd().EndsWith("]"))
                        {
                            yield return new ChatRuntimeEvent.ToolStatus(td.Text.Trim());
                        }
                        else
                        {
                            fullResponse.Append(td.Text);
                            yield return new ChatRuntimeEvent.TokenDelta(td.Text);
                        }
                        break;

                    case Hermes.Agent.LLM.StreamEvent.ThinkingDelta tk:
                        yield return new ChatRuntimeEvent.ThinkingDelta(tk.Text);
                        break;

                    case Hermes.Agent.LLM.StreamEvent.ToolUseStart ts:
                        yield return new ChatRuntimeEvent.ToolUseStart(ts.Id, ts.Name);
                        break;

                    case Hermes.Agent.LLM.StreamEvent.ToolUseDelta tdj:
                        yield return new ChatRuntimeEvent.ToolUseDelta(tdj.Id, tdj.PartialJson);
                        break;

                    case Hermes.Agent.LLM.StreamEvent.ToolUseComplete tc:
                        yield return new ChatRuntimeEvent.ToolUseComplete(tc.Id, tc.Name, tc.Arguments);
                        break;

                    case Hermes.Agent.LLM.StreamEvent.MessageComplete mc when mc.Usage is not null:
                        yield return new ChatRuntimeEvent.Usage(mc.Usage, mc.StopReason);
                        break;

                    case Hermes.Agent.LLM.StreamEvent.StreamError err:
                        turnStatus = TurnStatus.Failed;
                        turnError = err.Error.Message;
                        await AppendTimelineItemAsync(
                            turn,
                            TurnItemKind.Error,
                            TurnItemStatus.Failed,
                            err.Error.Message,
                            role: null,
                            metadata: new Dictionary<string, string> { ["code"] = err.Code.ToString() });
                        yield return new ChatRuntimeEvent.Error(new ChatRuntimeError(
                            err.Error.Message,
                            Code: err.Code.ToString(),
                            Retryable: err.Code is not ProviderErrorCode.ProviderAuth,
                            SuggestedAction: err.Code switch
                            {
                                ProviderErrorCode.ProviderAuth => "Open Settings and check the provider API key.",
                                ProviderErrorCode.RateLimit => "Retry later or switch models.",
                                ProviderErrorCode.ProviderTimeout => "Retry or switch providers.",
                                _ => "Retry the request."
                            }));
                        break;
                }
            }
        }
        finally
        {
            // Save response (partial or complete) — handles normal completion and cancellation.
            // Always save to avoid dangling user messages in the session, even if response is empty.
            // Guard against null — session may have been deleted/reset during streaming.
            if (_currentSession is not null &&
                _currentSession.Messages.LastOrDefault()?.Role != "assistant")
            {
                var assistantMsg = new Message { Role = "assistant", Content = fullResponse.ToString() };
                _currentSession.AddMessage(assistantMsg);
                await _transcriptStore.SaveMessageAsync(_currentSession.Id, assistantMsg, CancellationToken.None);
            }

            await CompleteTimelineTurnAsync(turn, fullResponse.ToString(), turnStatus, turnError);
        }

        if (_currentSession is not null)
            yield return new ChatRuntimeEvent.Completed(_currentSession.Id);
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamStructuredAsync(
        string message,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in StreamRuntimeAsync(message, ct))
        {
            switch (evt)
            {
                case ChatRuntimeEvent.TokenDelta token:
                    yield return new ChatStreamEvent(ChatStreamEventType.Token, token.Text);
                    break;
                case ChatRuntimeEvent.ThinkingDelta thinking:
                    yield return new ChatStreamEvent(ChatStreamEventType.Thinking, thinking.Text);
                    break;
                case ChatRuntimeEvent.ToolStatus toolStatus:
                    yield return new ChatStreamEvent(ChatStreamEventType.Thinking, toolStatus.Text);
                    break;
                case ChatRuntimeEvent.ToolUseStart toolStart:
                    yield return new ChatStreamEvent(
                        ChatStreamEventType.ToolStart, string.Empty,
                        ToolName: toolStart.Name, ToolCallId: toolStart.Id);
                    break;
                case ChatRuntimeEvent.ToolUseDelta toolDelta:
                    yield return new ChatStreamEvent(
                        ChatStreamEventType.ToolDelta, toolDelta.PartialJson,
                        ToolCallId: toolDelta.Id);
                    break;
                case ChatRuntimeEvent.ToolUseComplete toolComplete:
                    yield return new ChatStreamEvent(
                        ChatStreamEventType.ToolComplete, toolComplete.Arguments.GetRawText(),
                        ToolName: toolComplete.Name, ToolCallId: toolComplete.Id);
                    break;
                case ChatRuntimeEvent.Usage usage:
                    yield return new ChatStreamEvent(
                        ChatStreamEventType.Usage, usage.StopReason ?? string.Empty,
                        Usage: usage.Stats);
                    break;
                case ChatRuntimeEvent.Error error:
                    yield return new ChatStreamEvent(ChatStreamEventType.Error, error.Detail.Message);
                    break;
            }
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

        await UpsertTimelineThreadAsync(ct);
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
        _permissionManager.Mode = mode;
        CurrentPermissionMode = mode;
    }

    public void ClearRememberedPermissionsForWorkspace()
    {
        _permissionManager.ClearAlwaysAllowRules();
        _permissionRuleStore.ClearAlwaysAllowRules();
    }

    public void ClearRememberedWorkspacePermissions() => ClearRememberedPermissionsForWorkspace();

    // ── Tool Registration ──

    public void RegisterTool(ITool tool) => _agent.RegisterTool(tool);

    private async Task UpsertTimelineThreadAsync(CancellationToken ct)
    {
        if (_timelineStore is null || _currentSession is null)
            return;

        try
        {
            var firstUserMessage = _currentSession.Messages.FirstOrDefault(message => message.Role == "user")?.Content;
            await _timelineStore.GetOrCreateThreadAsync(
                _currentSession.Id,
                _currentSession.Platform ?? "desktop",
                workspaceRoot: null,
                provider: null,
                model: null,
                title: firstUserMessage,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to upsert timeline thread for session {SessionId}", _currentSession?.Id);
        }
    }

    private async Task<TurnRecord?> StartTimelineTurnAsync(string message, CancellationToken ct)
    {
        if (_timelineStore is null || _currentSession is null)
            return null;

        try
        {
            var turn = await _timelineStore.StartTurnAsync(
                _currentSession.Id,
                _currentSession.Platform ?? "desktop",
                message,
                workspaceRoot: null,
                provider: null,
                model: null,
                ct);

            await _timelineStore.AppendItemAsync(
                turn.ThreadId,
                turn.TurnId,
                TurnItemKind.UserMessage,
                TurnItemStatus.Completed,
                message,
                role: "user",
                messageIndex: _currentSession.Messages.Count,
                ct: ct);

            return turn;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to start timeline turn for session {SessionId}", _currentSession?.Id);
            return null;
        }
    }

    private async Task AppendTimelineItemAsync(
        TurnRecord? turn,
        TurnItemKind kind,
        TurnItemStatus status,
        string contentSummary,
        string? role,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (_timelineStore is null || turn is null)
            return;

        try
        {
            await _timelineStore.AppendItemAsync(
                turn.ThreadId,
                turn.TurnId,
                kind,
                status,
                contentSummary,
                role: role,
                metadata: metadata,
                ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append timeline item for turn {TurnId}", turn.TurnId);
        }
    }

    private async Task CompleteTimelineTurnAsync(
        TurnRecord? turn,
        string? assistantResponse,
        TurnStatus status,
        string? error)
    {
        if (_timelineStore is null || turn is null)
            return;

        try
        {
            if (!string.IsNullOrEmpty(assistantResponse))
            {
                var itemStatus = status switch
                {
                    TurnStatus.Completed => TurnItemStatus.Completed,
                    TurnStatus.Canceled => TurnItemStatus.Canceled,
                    _ => TurnItemStatus.Failed
                };

                await _timelineStore.AppendItemAsync(
                    turn.ThreadId,
                    turn.TurnId,
                    TurnItemKind.AssistantMessage,
                    itemStatus,
                    assistantResponse,
                    role: "assistant",
                    ct: CancellationToken.None);
            }

            await _timelineStore.CompleteTurnAsync(turn.ThreadId, turn.TurnId, status, error, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to complete timeline turn {TurnId}", turn.TurnId);
        }
    }

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
//
// These desktop-side types are a thin alias over the public Hermes.Agent.LLM.ChatStreamEnvelope.
// The enum is duplicated only so existing call sites like `ChatStreamEventType.Token` keep
// compiling. Mapping is one-to-one with ChatStreamEventKind.

internal enum ChatStreamEventType
{
    Token,
    Thinking,
    ToolStart,
    ToolDelta,
    ToolComplete,
    Usage,
    Error,
}

internal sealed record ChatStreamEvent(
    ChatStreamEventType Type,
    string Text,
    string? ToolName = null,
    string? ToolCallId = null,
    Hermes.Agent.LLM.UsageStats? Usage = null);
