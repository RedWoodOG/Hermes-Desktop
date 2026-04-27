using System.Collections.Concurrent;
using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Hermes.Agent.Soul;
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
    private readonly SoulService? _soulService;

    private readonly ConcurrentDictionary<string, SessionState> _sessionStates = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();

    /// <summary>
    /// Creates a new ContextManager wired to the given transcript store, LLM client, and budget.
    /// </summary>
    public ContextManager(
        TranscriptStore transcripts,
        IChatClient chatClient,
        TokenBudget budget,
        PromptBuilder promptBuilder,
        ILogger<ContextManager> logger,
        SoulService? soulService = null)
    {
        _transcripts = transcripts;
        _chatClient = chatClient;
        _budget = budget;
        _promptBuilder = promptBuilder;
        _logger = logger;
        _soulService = soulService;
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
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));

        await sessionLock.WaitAsync(ct);
        try
        {
            // Load transcript from archive — empty list for brand-new sessions
            List<Message> allMessages;
            if (_transcripts.SessionExists(sessionId))
                allMessages = await _transcripts.LoadSessionAsync(sessionId, ct);
            else
                allMessages = new List<Message>();

            // Split into recent window and evicted older messages
            var recentTurns = _budget.TrimToRecentWindow(allMessages);
            var evictedMessages = _budget.GetEvictedMessages(allMessages);

            // ── De-dupe trailing user message ──
            // Agent.ChatAsync persists the current user message to the transcript
            // BEFORE calling PrepareContextAsync, so it appears at the tail of
            // recentTurns. PromptBuilder.ToOpenAiMessages then re-appends
            // CurrentUserMessage as its final layer, which would duplicate the
            // turn and confuse the model (it commonly responds twice or treats
            // the prior turn as if it were a separate question). Strip only the
            // LAST matching user message — earlier user messages with the same
            // content are legitimate repeat asks and must be preserved.
            if (recentTurns.Count > 0)
            {
                var tail = recentTurns[^1];
                if (string.Equals(tail.Role, "user", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(tail.Content, userMessage, StringComparison.Ordinal))
                {
                    recentTurns = recentTurns.Take(recentTurns.Count - 1).ToList();
                }
            }

            // Estimate current token usage (all layers that will be sent)
            // Use EstimateMessageTokens for standalone text that PromptBuilder wraps in Message objects
            var systemTokens = _budget.EstimateMessageTokens("system", _promptBuilder.SystemPrompt);
            var stateTokens = state.EstimateTokens();
            var recentTokens = _budget.EstimateTokens(recentTurns);
            var retrievedTokens = EstimateRetrievedContextTokens(retrievedContext);
            var userTokens = _budget.EstimateMessageTokens("user", userMessage);
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
            else if (evictedMessages.Count > 0 && IsSummaryStaleBeyond(state, turnsStalenessThreshold: 10))
            {
                // Summary is stale — re-summarize to capture recent evictions
                await SummarizeEvictedAsync(state, evictedMessages, ct);
            }

            // Recompute pressure after summarization may have grown state.Summary
            var postSummarizeStateTokens = state.EstimateTokens();
            var postTotalTokens = systemTokens + postSummarizeStateTokens + recentTokens + retrievedTokens + userTokens;
            var postPressure = _budget.GetPressure(postTotalTokens);

            // Under critical pressure, compact the state itself
            if (postPressure == BudgetPressure.Critical)
            {
                state.Compact(maxDecisions: 5, maxQuestions: 3, maxEntities: 10);
                _logger.LogWarning("Critical budget pressure — compacted session state");
            }

            // Increment turn count only after all async work succeeds
            state.TurnCount++;

            // Load soul context (identity, user profile, project rules, learned behaviors)
            string? soulContext = null;
            if (_soulService is not null)
            {
                try
                {
                    soulContext = await _soulService.AssembleSoulContextAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load soul context — continuing without it");
                }
            }

            // Build the prompt
            var packet = _promptBuilder.Build(new BuildRequest
            {
                State = state,
                CurrentUserMessage = userMessage,
                RecentTurns = recentTurns,
                RetrievedContext = retrievedContext,
                SoulContext = soulContext
            });

            return _promptBuilder.ToOpenAiMessages(packet);
        }
        finally
        {
            sessionLock.Release();
        }
    }

    /// <summary>
    /// Updates session state after receiving an LLM response.
    /// Call this after each successful LLM completion to keep state current.
    /// </summary>
    public async Task UpdateAfterResponseAsync(string sessionId, string? responseId = null, CancellationToken ct = default)
    {
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(ct);
        try
        {
            if (_sessionStates.TryGetValue(sessionId, out var state))
            {
                state.PreviousResponseId = responseId;
            }
        }
        finally
        {
            sessionLock.Release();
        }
    }

    /// <summary>
    /// Records a decision made during the conversation.
    /// </summary>
    public async Task RecordDecisionAsync(string sessionId, string what, string why, CancellationToken ct = default)
    {
        var state = GetOrCreateState(sessionId);
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(ct);
        try
        {
            state.Decisions.Add(new Decision
            {
                What = what,
                Why = why,
                TurnNumber = state.TurnCount
            });
        }
        finally
        {
            sessionLock.Release();
        }
    }

    /// <summary>
    /// Updates the active goal for a session.
    /// </summary>
    public async Task SetActiveGoalAsync(string sessionId, string goal, CancellationToken ct = default)
    {
        var state = GetOrCreateState(sessionId);
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(ct);
        try
        {
            state.ActiveGoal = goal;
        }
        finally
        {
            sessionLock.Release();
        }
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
        _sessionLocks.TryRemove(sessionId, out _);
    }

    /// <summary>
    /// Returns true if the summary is stale — i.e. more than the given number of turns
    /// have elapsed since the summary was last updated. Uses CoveredThroughTurn for gap detection.
    /// </summary>
    private static bool IsSummaryStaleBeyond(SessionState state, int turnsStalenessThreshold)
    {
        if (string.IsNullOrEmpty(state.Summary.Content))
            return false;

        var turnsSinceLastSummary = state.TurnCount - state.Summary.CoveredThroughTurn;
        return turnsSinceLastSummary > turnsStalenessThreshold;
    }

    /// <summary>
    /// Estimates token cost for retrieved context chunks (~4 chars per token).
    /// </summary>
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
    /// <summary>
    /// Uses the LLM to compress evicted messages into a summary paragraph.
    /// INV-002: When a previous summary exists, instructs the LLM to UPDATE it
    /// rather than regenerate from scratch, using a structured template.
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

        var hasPreviousSummary = !string.IsNullOrEmpty(state.Summary.Content);

        // INV-002: Iterative summary — update existing summary instead of regenerating
        var systemContent = hasPreviousSummary
            ? "You are a conversation summarizer. You have an existing summary to UPDATE. "
              + "Merge the new information into the existing summary. Do not start over. "
              + "Use the structured template provided. Preserve important names, paths, and technical details."
            : "You are a conversation summarizer. Produce a concise structured summary. "
              + "Preserve important names, paths, and technical details. Do not include pleasantries or filler.";

        var userContent = hasPreviousSummary
            ? $"Previous summary to UPDATE:\n{state.Summary.Content}\n\n"
              + $"New conversation segment to incorporate:\n\n{transcript}\n\n"
              + "Produce the updated summary using this template:\n"
              + "- Goal: [update from previous]\n"
              + "- Progress: [what was accomplished]\n"
              + "- Decisions: [key choices made]\n"
              + "- Files: [files touched]\n"
              + "- Next: [what's needed next]"
            : $"Summarize this conversation segment:\n\n{transcript}\n\n"
              + "Use this template:\n"
              + "- Goal: [the current objective]\n"
              + "- Progress: [what was accomplished]\n"
              + "- Decisions: [key choices made]\n"
              + "- Files: [files touched]\n"
              + "- Next: [what's needed next]";

        var summarizePrompt = new List<Message>
        {
            new() { Role = "system", Content = systemContent },
            new() { Role = "user", Content = userContent }
        };

        try
        {
            var summary = await _chatClient.CompleteAsync(summarizePrompt, ct);
            state.Summary.Content = summary.Trim();
            state.Summary.CoveredThroughTurn = state.TurnCount;

            _logger.LogInformation(
                "Summarized {Count} evicted messages into {Tokens} est. tokens (iterative={Iterative})",
                evictedMessages.Count,
                _budget.EstimateTokens(summary),
                hasPreviousSummary);
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
