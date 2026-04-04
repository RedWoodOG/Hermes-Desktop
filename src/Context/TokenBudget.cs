using Hermes.Agent.Core;

namespace Hermes.Agent.Context;

/// <summary>
/// Enforces hard limits on context size to prevent context explosion.
/// When token usage crosses thresholds, triggers summarization and trimming.
/// </summary>
public sealed class TokenBudget
{
    private readonly int _maxTokens;
    private readonly int _recentTurnWindow;

    /// <summary>
    /// Creates a token budget with the given ceiling. Thresholds scale proportionally.
    /// </summary>
    /// <param name="maxTokens">Hard token ceiling (must be greater than zero).</param>
    /// <param name="recentTurnWindow">Number of recent turns to keep (must be non-negative).</param>
    public TokenBudget(int maxTokens = 8000, int recentTurnWindow = 6)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxTokens, 0);
        ArgumentOutOfRangeException.ThrowIfNegative(recentTurnWindow);
        _maxTokens = maxTokens;
        _recentTurnWindow = recentTurnWindow;
    }

    /// <summary>Hard ceiling on total tokens sent per turn.</summary>
    public int MaxTokens => _maxTokens;

    /// <summary>75% of max — crossing this triggers summarization of older turns.</summary>
    public int SummaryThreshold => (int)(_maxTokens * 0.75);

    /// <summary>94% of max — crossing this triggers aggressive trimming.</summary>
    public int CriticalThreshold => (int)(_maxTokens * 0.94);

    /// <summary>Number of most recent turns to keep in the context window.</summary>
    public int RecentTurnWindow => _recentTurnWindow;

    /// <summary>
    /// Estimates the token count for a sequence of messages.
    /// Uses ~4 chars/token heuristic with overhead for role tags and JSON framing.
    /// </summary>
    public int EstimateTokens(IEnumerable<Message> messages)
    {
        var totalChars = 0;
        var messageCount = 0;

        foreach (var msg in messages)
        {
            totalChars += msg.Content.Length;
            totalChars += msg.Role.Length;
            messageCount++;
        }

        // ~4 chars per token for content, plus ~4 tokens per message for framing
        return (totalChars / 4) + (messageCount * 4);
    }

    /// <summary>
    /// Estimates the token count for a single string (system prompt, JSON blob, etc).
    /// </summary>
    public int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return text.Length / 4;
    }

    /// <summary>
    /// Determines the current budget pressure level based on total token usage.
    /// </summary>
    public BudgetPressure GetPressure(int currentTokens)
    {
        if (currentTokens >= CriticalThreshold) return BudgetPressure.Critical;
        if (currentTokens >= SummaryThreshold) return BudgetPressure.High;
        return BudgetPressure.Normal;
    }

    /// <summary>
    /// Returns the number of tokens remaining before hitting the hard ceiling.
    /// </summary>
    public int RemainingTokens(int currentTokens) => Math.Max(0, MaxTokens - currentTokens);

    /// <summary>
    /// Trims a message list to fit within the recent turn window.
    /// Returns the most recent N turns (a turn = one user + one assistant message).
    /// </summary>
    public List<Message> TrimToRecentWindow(List<Message> messages)
    {
        if (messages.Count <= RecentTurnWindow * 2)
            return messages;

        // Keep last N*2 messages (each turn is user+assistant)
        var startIndex = messages.Count - (RecentTurnWindow * 2);
        return messages.GetRange(startIndex, messages.Count - startIndex);
    }

    /// <summary>
    /// Returns the messages that would be evicted by trimming to the recent window.
    /// These are candidates for summarization.
    /// </summary>
    public List<Message> GetEvictedMessages(List<Message> messages)
    {
        if (messages.Count <= RecentTurnWindow * 2)
            return new List<Message>();

        var evictCount = messages.Count - (RecentTurnWindow * 2);
        return messages.GetRange(0, evictCount);
    }
}

public enum BudgetPressure
{
    /// <summary>Under 75% — no action needed.</summary>
    Normal,

    /// <summary>75-94% — should summarize older turns.</summary>
    High,

    /// <summary>Over 94% — aggressive trimming required.</summary>
    Critical
}
