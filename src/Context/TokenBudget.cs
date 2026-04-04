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
    /// Includes role framing, content, and optional tool metadata.
    /// </summary>
    public int EstimateTokens(IEnumerable<Message> messages)
    {
        var totalChars = 0;
        var messageCount = 0;

        foreach (var msg in messages)
        {
            totalChars += msg.Content.Length;
            totalChars += msg.Role.Length;
            totalChars += msg.ToolCallId?.Length ?? 0;
            totalChars += msg.ToolName?.Length ?? 0;
            messageCount++;
        }

        // ~4 chars per token for content, plus ~4 tokens per message for role/framing
        return (int)Math.Ceiling(totalChars / 4.0) + (messageCount * 4);
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
    /// Estimates the token count for a standalone text layer that will be wrapped
    /// in a Message object (e.g. system prompt, user message). Accounts for role
    /// framing overhead that the raw string overload misses.
    /// </summary>
    public int EstimateMessageTokens(string role, string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var totalChars = role.Length + text.Length;
        return (int)Math.Ceiling(totalChars / 4.0) + 4;
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
    /// Scans from the end counting user messages as turn starts, so tool/system
    /// messages within a turn are kept together rather than split mid-turn.
    /// </summary>
    public List<Message> TrimToRecentWindow(List<Message> messages)
    {
        var startIndex = FindRecentWindowStart(messages);
        return startIndex == 0
            ? new List<Message>(messages)
            : messages.GetRange(startIndex, messages.Count - startIndex);
    }

    /// <summary>
    /// Returns the messages that would be evicted by trimming to the recent window.
    /// These are candidates for summarization.
    /// </summary>
    public List<Message> GetEvictedMessages(List<Message> messages)
    {
        var startIndex = FindRecentWindowStart(messages);
        return startIndex == 0
            ? new List<Message>()
            : messages.GetRange(0, startIndex);
    }

    /// <summary>
    /// Finds the index where the recent turn window starts by scanning backwards
    /// and counting "user" messages as turn boundaries.
    /// </summary>
    private int FindRecentWindowStart(IReadOnlyList<Message> messages)
    {
        if (messages.Count == 0)
            return 0;
        if (RecentTurnWindow == 0)
            return messages.Count;

        var userTurnsSeen = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (string.Equals(messages[i].Role, "user", StringComparison.Ordinal))
            {
                userTurnsSeen++;
                if (userTurnsSeen == RecentTurnWindow)
                    return i;
            }
        }

        // Fewer turns than the window — keep everything
        return 0;
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
