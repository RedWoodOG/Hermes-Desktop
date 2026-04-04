using System.Collections.Concurrent;
using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Hermes.Agent.Transcript;
using Microsoft.Extensions.Logging;

namespace Hermes.Agent.Context;

/// <summary>
/// Main orchestrator for the Context Runtime.
/// Replaces the naive "send all messages" pattern with:
///   conversation history = archive (TranscriptStore)
///   session state = active memory (SessionState)
///   retrieval = selective recall (on-demand)
/// </summary>
public sealed class ContextManager
{
    private readonly TranscriptStore _transcripts;
    private readonly IChatClient _chatClient;
    private readonly TokenBudget _budget;
    private readonly PromptBuilder _promptBuilder;
    private readonly ILogger<ContextManager> _logger;

    private readonly ConcurrentDictionary<string, SessionState> _sessionStates = new();

    public ContextManager(
        TranscriptStore transcripts,
        IChatClient chatClient,
        TokenBudget budget,
        PromptBuilder promptBuilder,
        ILogger<ContextManager> logger)
    {
        _transcripts = transcripts;
        _chatClient = chatClient;
        _budget = budget;
        _promptBuilder = promptBuilder;
        _logger = logger;
    }

    /// <summary>
    /// Prepares an optimized context for the LLM call, replacing the naive full-history pattern.
    ///
    /// Flow:
    /// 1. Load or create session state
    /// 2. Get recent turns (last N only) from transcript
    /// 3. Check token budget pressure
    /// 4. If over threshold, summarize evicted turns
    /// 5. Build cache-safe prompt packet
    /// 6. Return OpenAI-compatible message list
    /// </summary>
    public async Task<List<Message>> PrepareContextAsync(
        string sessionId,
        string userMessage,
        List<string>? retrievedContext,
        CancellationToken ct)
    {
        var state = GetOrCreateState(sessionId);

        // Load transcript from archive — empty list for brand-new sessions
        List<Message> allMessages;
        if (_transcripts.SessionExists(sessionId))
            allMessages = await _transcripts.LoadSessionAsync(sessionId, ct);
        else
            allMessages = new List<Message>();

        // Split into recent window and evicted older messages
        var recentTurns = _budget.TrimToRecentWindow(allMessages);
        var evictedMessages = _budget.GetEvictedMessages(allMessages);

        // Estimate current token usage (all layers that will be sent)
        var systemTokens = _budget.EstimateTokens(_promptBuilder.SystemPrompt);
        var stateTokens = state.EstimateTokens();
        var recentTokens = _budget.EstimateTokens(recentTurns);
        var retrievedTokens = EstimateRetrievedContextTokens(retrievedContext);
        var userTokens = _budget.EstimateTokens(userMessage);
        var totalTokens = systemTokens + stateTokens + recentTokens + retrievedTokens + userTokens;

        var pressure = _budget.GetPressure(totalTokens);

        _logger.LogDebug(
            "Context budget: {Total}/{Max} tokens ({Pressure}), {Recent} recent, {Evicted} evicted",
            totalTokens, _budget.MaxTokens, pressure, recentTurns.Count, evictedMessages.Count);

        // Summarize evicted messages if we have any and need to
        if (evictedMessages.Count > 0 && pressure >= BudgetPressure.High)
        {
            await SummarizeEvictedAsync(state, evictedMessages, ct);
        }
        else if (evictedMessages.Count > 0 && string.IsNullOrEmpty(state.Summary.Content))
        {
            // First time we evict messages — create initial summary
            await SummarizeEvictedAsync(state, evictedMessages, ct);
        }

        // Under critical pressure, compact the state itself
        if (pressure == BudgetPressure.Critical)
        {
            state.Compact(maxDecisions: 5, maxQuestions: 3, maxEntities: 10);
            _logger.LogWarning("Critical budget pressure — compacted session state");
        }

        // Increment turn count only after all async work succeeds
        state.TurnCount++;

        // Build the prompt
        var packet = _promptBuilder.Build(new BuildRequest
        {
            State = state,
            CurrentUserMessage = userMessage,
            RecentTurns = recentTurns,
            RetrievedContext = retrievedContext
        });

        return _promptBuilder.ToOpenAiMessages(packet);
    }

    /// <summary>
    /// Updates session state after receiving an LLM response.
    /// Call this after each successful LLM completion to keep state current.
    /// </summary>
    public void UpdateAfterResponse(string sessionId, string? responseId = null)
    {
        if (_sessionStates.TryGetValue(sessionId, out var state))
        {
            state.PreviousResponseId = responseId;
        }
    }

    /// <summary>
    /// Records a decision made during the conversation.
    /// </summary>
    public void RecordDecision(string sessionId, string what, string why)
    {
        var state = GetOrCreateState(sessionId);
        state.Decisions.Add(new Decision
        {
            What = what,
            Why = why,
            TurnNumber = state.TurnCount
        });
    }

    /// <summary>
    /// Updates the active goal for a session.
    /// </summary>
    public void SetActiveGoal(string sessionId, string goal)
    {
        var state = GetOrCreateState(sessionId);
        state.ActiveGoal = goal;
    }

    /// <summary>
    /// Gets or creates session state for a given session ID.
    /// </summary>
    public SessionState GetOrCreateState(string sessionId)
    {
        return _sessionStates.GetOrAdd(sessionId, _ => new SessionState());
    }

    /// <summary>
    /// Removes session state from memory (transcript stays on disk via TranscriptStore).
    /// </summary>
    public void EvictState(string sessionId)
    {
        _sessionStates.TryRemove(sessionId, out _);
    }

    private int EstimateRetrievedContextTokens(List<string>? retrievedContext)
    {
        if (retrievedContext is null or { Count: 0 }) return 0;
        var totalChars = 0;
        foreach (var chunk in retrievedContext)
            totalChars += chunk.Length;
        return totalChars / 4;
    }

    /// <summary>
    /// Uses the LLM to compress evicted messages into a summary paragraph.
    /// This summary becomes part of SessionState and survives across turns.
    /// </summary>
    private async Task SummarizeEvictedAsync(
        SessionState state,
        List<Message> evictedMessages,
        CancellationToken ct)
    {
        var transcript = string.Join("\n", evictedMessages.Select(m => $"{m.Role}: {m.Content}"));

        // Truncate if the evicted block is huge — keep the NEWEST messages (tail)
        // since they're closest to the current context and most relevant to summarize
        if (transcript.Length > 4000)
            transcript = "[...truncated]\n" + transcript[^4000..];

        var existingSummary = string.IsNullOrEmpty(state.Summary.Content)
            ? ""
            : $"Previous summary: {state.Summary.Content}\n\n";

        var summarizePrompt = new List<Message>
        {
            new()
            {
                Role = "system",
                Content = "You are a conversation summarizer. Produce a concise summary (2-4 sentences) of the key points, decisions, and outcomes. Preserve important names, paths, and technical details. Do not include pleasantries or filler."
            },
            new()
            {
                Role = "user",
                Content = $"{existingSummary}Summarize this conversation segment:\n\n{transcript}"
            }
        };

        try
        {
            var summary = await _chatClient.CompleteAsync(summarizePrompt, ct);
            state.Summary.Content = summary.Trim();
            state.Summary.CoveredThroughTurn = state.TurnCount - 1;

            _logger.LogInformation(
                "Summarized {Count} evicted messages into {Tokens} est. tokens",
                evictedMessages.Count,
                _budget.EstimateTokens(summary));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to summarize evicted messages — keeping stale summary");
        }
    }
}
