namespace Hermes.Agent.Compaction;

using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Microsoft.Extensions.Logging;

/// <summary>
/// Manages context window compaction to stay within token budgets.
/// </summary>
public sealed class CompactionManager
{
    private readonly IChatClient _chatClient;
    private readonly ITokenCounter _tokenCounter;
    private readonly CompactionConfig _config;
    private readonly ILogger<CompactionManager> _logger;

    /// <summary>Tracks last compression failure time for 600-second cooldown (INV-002).</summary>
    private DateTime? _lastCompressionFailureTime;

    /// <summary>Cooldown period after a compression failure before retrying.</summary>
    private static readonly TimeSpan CompressionCooldown = TimeSpan.FromSeconds(600);

    public CompactionManager(
        IChatClient chatClient,
        ITokenCounter? tokenCounter = null,
        CompactionConfig? config = null,
        ILogger<CompactionManager>? logger = null)
    {
        _chatClient = chatClient;
        _tokenCounter = tokenCounter ?? new TiktokenCounter();
        _config = config ?? new CompactionConfig();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CompactionManager>.Instance;
    }

    /// <summary>
    /// Returns true if compression is currently in cooldown after a recent failure.
    /// </summary>
    public bool IsInCompressionCooldown()
    {
        if (_lastCompressionFailureTime is null) return false;
        var elapsed = DateTime.UtcNow - _lastCompressionFailureTime.Value;
        return elapsed < CompressionCooldown;
    }
    
    /// <summary>
    /// Check if compaction is needed based on token budget.
    /// </summary>
    public CompactionCheckResult CheckNeedsCompaction(IReadOnlyList<Message> messages, int? systemPromptTokens = null)
    {
        var totalTokens = systemPromptTokens ?? 0;
        
        foreach (var msg in messages)
        {
            totalTokens += _tokenCounter.CountTokens(msg.Content);
        }
        
        var threshold = (int)(_config.ContextWindowSize * _config.CompactionThreshold);
        var criticalThreshold = (int)(_config.ContextWindowSize * _config.CriticalThreshold);
        
        if (totalTokens >= criticalThreshold)
        {
            return new CompactionCheckResult(
                CompactionUrgency.Critical,
                totalTokens,
                _config.ContextWindowSize,
                "Context window critically full - immediate compaction required"
            );
        }
        
        if (totalTokens >= threshold)
        {
            return new CompactionCheckResult(
                CompactionUrgency.Needed,
                totalTokens,
                _config.ContextWindowSize,
                "Context window approaching limit - compaction recommended"
            );
        }
        
        var warningThreshold = (int)(_config.ContextWindowSize * _config.WarningThreshold);
        if (totalTokens >= warningThreshold)
        {
            return new CompactionCheckResult(
                CompactionUrgency.Warning,
                totalTokens,
                _config.ContextWindowSize,
                "Context window usage high - consider compaction soon"
            );
        }
        
        return new CompactionCheckResult(
            CompactionUrgency.NotNeeded,
            totalTokens,
            _config.ContextWindowSize,
            null
        );
    }
    
    /// <summary>
    /// Perform full compaction - summarize entire conversation.
    /// </summary>
    public async Task<CompactionResult> CompactFullAsync(
        IReadOnlyList<Message> messages,
        string? systemPrompt = null,
        string? previousSummary = null,
        CancellationToken ct = default)
    {
        // INV-002: 600-second cooldown after compression failure
        if (IsInCompressionCooldown())
        {
            _logger.LogWarning(
                "Compression skipped — in cooldown until {CooldownEnd}",
                _lastCompressionFailureTime!.Value.Add(CompressionCooldown));
            return new CompactionResult(messages, 0, 0, CompactionType.Full);
        }

        _logger.LogInformation("Starting full compaction of {Count} messages", messages.Count);

        try
        {
            var compactionPrompt = BuildCompactionPrompt(messages, CompactionType.Full, previousSummary);
            var summary = await _chatClient.CompleteAsync(new[]
            {
                new Message { Role = "user", Content = compactionPrompt }
            }, ct);

            var summaryMessage = new Message
            {
                Role = "assistant",
                Content = $"[Conversation Summary]\n{summary}"
            };

            // INV-002: Sanitize orphaned tool results after compaction
            var compactedMessages = SanitizeOrphanedToolResults(new List<Message> { summaryMessage });

            var originalTokens = messages.Sum(m => _tokenCounter.CountTokens(m.Content));
            var newTokens = _tokenCounter.CountTokens(summary);

            _logger.LogInformation("Compaction complete: {Original} -> {New} tokens ({Percent:P0} reduction)",
                originalTokens, newTokens, 1 - ((double)newTokens / originalTokens));

            // Success — reset cooldown timer
            _lastCompressionFailureTime = null;

            return new CompactionResult(
                compactedMessages,
                originalTokens,
                newTokens,
                CompactionType.Full
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // INV-002: Record failure time for cooldown
            _lastCompressionFailureTime = DateTime.UtcNow;
            _logger.LogError(ex, "Compression failed — entering 600s cooldown");
            return new CompactionResult(messages, 0, 0, CompactionType.Full);
        }
    }
    
    /// <summary>
    /// Perform partial compaction - summarize only older messages.
    /// </summary>
    public async Task<CompactionResult> CompactPartialAsync(
        IReadOnlyList<Message> messages,
        int keepRecentCount,
        string? systemPrompt = null,
        string? previousSummary = null,
        CancellationToken ct = default)
    {
        // INV-002: 600-second cooldown after compression failure
        if (IsInCompressionCooldown())
        {
            _logger.LogWarning(
                "Partial compression skipped — in cooldown until {CooldownEnd}",
                _lastCompressionFailureTime!.Value.Add(CompressionCooldown));
            return new CompactionResult(messages, 0, 0, CompactionType.Partial);
        }

        _logger.LogInformation("Starting partial compaction, keeping {Keep} recent messages", keepRecentCount);

        if (messages.Count <= keepRecentCount)
        {
            return new CompactionResult(messages, 0, 0, CompactionType.Partial);
        }

        var toCompact = messages.Take(messages.Count - keepRecentCount).ToList();
        var toKeep = messages.Skip(messages.Count - keepRecentCount).ToList();

        try
        {
            var compactionPrompt = BuildCompactionPrompt(toCompact, CompactionType.Partial, previousSummary);
            var summary = await _chatClient.CompleteAsync(new[]
            {
                new Message { Role = "user", Content = compactionPrompt }
            }, ct);

            var summaryMessage = new Message
            {
                Role = "assistant",
                Content = $"[Earlier Conversation Summary]\n{summary}"
            };

            var compactedMessages = new List<Message> { summaryMessage };
            compactedMessages.AddRange(toKeep);

            // INV-002: Sanitize orphaned tool results after compaction
            compactedMessages = SanitizeOrphanedToolResults(compactedMessages);

            var originalTokens = messages.Sum(m => _tokenCounter.CountTokens(m.Content));
            var newTokens = compactedMessages.Sum(m => _tokenCounter.CountTokens(m.Content));

            // Success — reset cooldown timer
            _lastCompressionFailureTime = null;

            return new CompactionResult(
                compactedMessages,
                originalTokens,
                newTokens,
                CompactionType.Partial
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // INV-002: Record failure time for cooldown
            _lastCompressionFailureTime = DateTime.UtcNow;
            _logger.LogError(ex, "Partial compression failed — entering 600s cooldown");
            return new CompactionResult(messages, 0, 0, CompactionType.Partial);
        }
    }
    
    /// <summary>
    /// Micro-compact - remove time-based content (old tool results, etc.).
    /// </summary>
    public CompactionResult CompactMicro(IReadOnlyList<Message> messages, TimeSpan? maxAge = null)
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(maxAge ?? _config.MicroCompactMaxAge);
        var result = new List<Message>();
        var removed = 0;
        
        foreach (var msg in messages)
        {
            // Keep user and assistant messages
            if (msg.Role is "user" or "assistant" or "system")
            {
                result.Add(msg);
                continue;
            }
            
            // Remove old tool results
            if (msg.Role == "tool" && msg.Timestamp < cutoff)
            {
                removed++;
                continue;
            }
            
            result.Add(msg);
        }
        
        var originalTokens = messages.Sum(m => _tokenCounter.CountTokens(m.Content));
        var newTokens = result.Sum(m => _tokenCounter.CountTokens(m.Content));
        
        return new CompactionResult(result, originalTokens, newTokens, CompactionType.Micro);
    }
    
    /// <summary>
    /// INV-002: Remove tool-result messages whose ToolCallId references a tool_call
    /// that was summarized away during compaction.
    /// </summary>
    private static List<Message> SanitizeOrphanedToolResults(List<Message> messages)
    {
        var survivingCallIds = new HashSet<string>();
        foreach (var msg in messages)
        {
            if (msg.ToolCalls is { Count: > 0 })
                foreach (var tc in msg.ToolCalls)
                    survivingCallIds.Add(tc.Id);
        }
        return messages.Where(m =>
            m.Role != "tool" ||
            string.IsNullOrEmpty(m.ToolCallId) ||
            survivingCallIds.Contains(m.ToolCallId)
        ).ToList();
    }

    private string BuildCompactionPrompt(IReadOnlyList<Message> messages, CompactionType type, string? previousSummary = null)
    {
        var sb = new System.Text.StringBuilder();

        // INV-002: Iterative summary — update previous summary instead of regenerating from scratch
        if (!string.IsNullOrEmpty(previousSummary))
        {
            sb.AppendLine("You have a previous conversation summary. UPDATE it with the new information below.");
            sb.AppendLine("Do not regenerate from scratch — merge new information into the existing summary.");
            sb.AppendLine();
            sb.AppendLine("Previous summary:");
            sb.AppendLine(previousSummary);
            sb.AppendLine();
            sb.AppendLine("New conversation segment to incorporate:");
        }
        else
        {
            sb.AppendLine(type switch
            {
                CompactionType.Full => "Summarize the following conversation concisely, preserving key information, decisions, and context needed for continuing the work:",
                CompactionType.Partial => "Summarize the earlier part of this conversation, preserving important context and decisions:",
                _ => "Summarize the following:"
            });
        }

        sb.AppendLine();
        sb.AppendLine("---");

        foreach (var msg in messages)
        {
            sb.AppendLine($"[{msg.Role.ToUpperInvariant()}]: {msg.Content}");
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("Provide a structured summary using this template:");
        sb.AppendLine("- Goal: [the current objective]");
        sb.AppendLine("- Progress: [what was accomplished]");
        sb.AppendLine("- Decisions: [key choices made]");
        sb.AppendLine("- Files: [files touched]");
        sb.AppendLine("- Next: [what's needed next]");

        return sb.ToString();
    }
}

/// <summary>
/// Result of compaction check.
/// </summary>
public sealed record CompactionCheckResult(
    CompactionUrgency Urgency,
    int CurrentTokens,
    int MaxTokens,
    string? Message)
{
    public double UsagePercent => (double)CurrentTokens / MaxTokens;
    public bool NeedsCompaction => Urgency >= CompactionUrgency.Needed;
}

/// <summary>
/// Urgency level for compaction.
/// </summary>
public enum CompactionUrgency
{
    NotNeeded,
    Warning,
    Needed,
    Critical
}

/// <summary>
/// Result of compaction operation.
/// </summary>
public sealed record CompactionResult(
    IReadOnlyList<Message> CompactedMessages,
    int OriginalTokens,
    int NewTokens,
    CompactionType Type)
{
    public double ReductionPercent => OriginalTokens > 0 
        ? 1 - ((double)NewTokens / OriginalTokens) 
        : 0;
    public int TokensSaved => OriginalTokens - NewTokens;
}

/// <summary>
/// Type of compaction.
/// </summary>
public enum CompactionType
{
    Full,
    Partial,
    Micro
}

/// <summary>
/// Configuration for compaction.
/// </summary>
public sealed class CompactionConfig
{
    public int ContextWindowSize { get; init; } = 200000;
    public double CompactionThreshold { get; init; } = 0.80;
    public double CriticalThreshold { get; init; } = 0.90;
    public double WarningThreshold { get; init; } = 0.70;
    public TimeSpan MicroCompactMaxAge { get; init; } = TimeSpan.FromMinutes(30);
    public int DefaultKeepRecentCount { get; init; } = 10;
}

/// <summary>
/// Token counter interface.
/// </summary>
public interface ITokenCounter
{
    int CountTokens(string text);
    int CountTokens(IReadOnlyList<Message> messages);
}

/// <summary>
/// Tiktoken-based token counter.
/// </summary>
public sealed class TiktokenCounter : ITokenCounter
{
    private readonly string _encodingName;
    
    public TiktokenCounter(string encodingName = "cl100k_base")
    {
        _encodingName = encodingName;
    }
    
    public int CountTokens(string text)
    {
        // Simplified approximation - real implementation would use Tiktoken library
        // Approximate: 1 token ≈ 4 characters for English text
        return (int)Math.Ceiling(text.Length / 4.0);
    }
    
    public int CountTokens(IReadOnlyList<Message> messages)
    {
        return messages.Sum(m => CountTokens(m.Content) + 4); // +4 for message overhead
    }
}
