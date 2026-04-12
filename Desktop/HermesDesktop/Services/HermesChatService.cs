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
    //
    // CRITICAL: Producer runs on thread pool via Task.Run to prevent WinUI 3
    // sync context deadlocks. The agent pipeline (StreamChatAsync → _chatClient.StreamAsync)
    // contains many awaits without .ConfigureAwait(false), any of which would otherwise
    // try to resume on the UI thread. By running the producer on a worker thread and
    // passing events through a Channel<T>, we guarantee the HTTP read never blocks
    // waiting for the UI thread to become available.

    public async IAsyncEnumerable<ChatStreamEvent> StreamStructuredAsync(
        string message,
        [EnumeratorCancellation] CancellationToken ct)
    {
        EnsureSession();
        _streamCts?.Dispose();
        _streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var streamToken = _streamCts.Token;

        var fullResponse = new System.Text.StringBuilder();
        var channel = System.Threading.Channels.Channel.CreateUnbounded<ChatStreamEvent>(
            new System.Threading.Channels.UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            });

        var currentSession = _currentSession!;

        // Producer — runs on thread pool, fully detached from UI sync context.
        var producerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in _agent.StreamChatAsync(message, currentSession, streamToken)
                    .ConfigureAwait(false))
                {
                    switch (evt)
                    {
                        case Hermes.Agent.LLM.StreamEvent.TokenDelta td:
                            // Tool-calling status messages (e.g. "[Calling tool: bash]") are
                            // informational — show in UI but don't accumulate into the saved response
                            if (td.Text.StartsWith("\n[Calling tool:") && td.Text.TrimEnd().EndsWith("]"))
                            {
                                await channel.Writer.WriteAsync(
                                    new ChatStreamEvent(ChatStreamEventType.Thinking, td.Text.Trim()),
                                    streamToken).ConfigureAwait(false);
                            }
                            else
                            {
                                fullResponse.Append(td.Text);
                                await channel.Writer.WriteAsync(
                                    new ChatStreamEvent(ChatStreamEventType.Token, td.Text),
                                    streamToken).ConfigureAwait(false);
                            }
                            break;

                        case Hermes.Agent.LLM.StreamEvent.ThinkingDelta tk:
                            await channel.Writer.WriteAsync(
                                new ChatStreamEvent(ChatStreamEventType.Thinking, tk.Text),
                                streamToken).ConfigureAwait(false);
                            break;

                        case Hermes.Agent.LLM.StreamEvent.StreamError err:
                            await channel.Writer.WriteAsync(
                                new ChatStreamEvent(ChatStreamEventType.Error, err.Error.Message),
                                streamToken).ConfigureAwait(false);
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on cancel — swallow so consumer can drain remaining events
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent stream failed");
                try
                {
                    await channel.Writer.WriteAsync(
                        new ChatStreamEvent(ChatStreamEventType.Error, ex.Message),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch { /* channel may be closed */ }
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, streamToken);

        // Consumer — yields events from channel on the UI thread.
        try
        {
            while (await channel.Reader.WaitToReadAsync(streamToken))
            {
                while (channel.Reader.TryRead(out var evt))
                    yield return evt;
            }

            // Ensure producer task is observed (propagates any non-OCE exceptions)
            try { await producerTask; }
            catch (OperationCanceledException) { }
        }
        finally
        {
            // Save response (partial or complete) — handles normal completion and cancellation.
            if (_currentSession is not null &&
                _currentSession.Messages.LastOrDefault()?.Role != "assistant")
            {
                var assistantMsg = new Message { Role = "assistant", Content = fullResponse.ToString() };
                _currentSession.AddMessage(assistantMsg);
                await _transcriptStore.SaveMessageAsync(_currentSession.Id, assistantMsg, CancellationToken.None);
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
