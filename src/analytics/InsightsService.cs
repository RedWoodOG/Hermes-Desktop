namespace Hermes.Agent.Analytics;

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hermes.Agent.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// ══════════════════════════════════════════════
// Insights / Analytics Service
// ══════════════════════════════════════════════
//
// Upstream ref: agent/insights.py
// Token consumption, cost estimates, tool usage patterns,
// activity trends, model/platform breakdowns.

/// <summary>
/// Tracks token usage, costs, tool invocations, and activity patterns.
/// Persists to JSON for dashboard display.
/// </summary>
public sealed class InsightsService
{
    private readonly string _dataPath;
    private readonly ILogger<InsightsService> _logger;
    private InsightsData _data;
    private readonly object _lock = new();

    public InsightsService(string dataDir, ILogger<InsightsService>? logger = null)
    {
        Directory.CreateDirectory(dataDir);
        _dataPath = Path.Combine(dataDir, "insights.json");
        _logger = logger ?? NullLogger<InsightsService>.Instance;
        _data = Load();
    }

    // ── Recording ──

    /// <summary>Record token usage for a turn.</summary>
    public void RecordTokens(string model, int inputTokens, int outputTokens)
    {
        lock (_lock)
        {
            _data.TotalInputTokens += inputTokens;
            _data.TotalOutputTokens += outputTokens;
            _data.TotalTurns++;

            var key = model ?? "unknown";
            if (!_data.TokensByModel.TryGetValue(key, out var modelStats))
            {
                modelStats = new ModelStats();
                _data.TokensByModel[key] = modelStats;
            }
            modelStats.InputTokens += inputTokens;
            modelStats.OutputTokens += outputTokens;
            modelStats.Turns++;

            // Estimate cost (rough, per-provider pricing)
            var cost = EstimateCost(model, inputTokens, outputTokens);
            _data.EstimatedCostUsd += cost;
            modelStats.EstimatedCostUsd += cost;

            // Daily breakdown
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            if (!_data.DailyTokens.TryGetValue(today, out var daily))
            {
                daily = new DailyStats();
                _data.DailyTokens[today] = daily;
            }
            daily.InputTokens += inputTokens;
            daily.OutputTokens += outputTokens;
            daily.Turns++;
        }
    }

    /// <summary>Record a tool invocation.</summary>
    public void RecordToolUse(string toolName, bool success, long durationMs)
    {
        lock (_lock)
        {
            if (!_data.ToolUsage.TryGetValue(toolName, out var stats))
            {
                stats = new ToolStats();
                _data.ToolUsage[toolName] = stats;
            }
            stats.TotalCalls++;
            if (success) stats.Successes++; else stats.Failures++;
            stats.TotalDurationMs += durationMs;
        }
    }

    /// <summary>
    /// Increments the total session count and records one session for the specified platform.
    /// </summary>
    /// <param name="platform">The platform identifier for the session (for example, "web" or "mobile").</param>
    public void RecordSession(string platform)
    {
        lock (_lock)
        {
            _data.TotalSessions++;
            if (!_data.SessionsByPlatform.TryGetValue(platform, out var count))
                count = 0;
            _data.SessionsByPlatform[platform] = count + 1;
        }
    }

    /// <summary>
    /// Increments the persisted Dreamer walks counter in the insights snapshot, initializing the Dreamer counters if they do not exist.
    /// </summary>
    public void RecordDreamerWalk() => BumpDreamer(d => d.Walks++);
    public void RecordDreamerDigest() => BumpDreamer(d => d.Digests++);
    public void RecordDreamerBuild() => BumpDreamer(d => d.Builds++);
    public void RecordDreamerSignal() => BumpDreamer(d => d.Signals++);
    public void RecordDreamerStartupFailure(Exception exception) => BumpDreamer(d =>
    {
        d.StartupFailures++;
        d.LastStartupFailureUtc = DateTimeOffset.UtcNow;
        d.LastStartupFailureMessage = exception.Message;
    });

    /// <summary>
    /// Ensures the service's Dreamer stats object exists and applies the provided delegate to update it under the instance lock.
    /// </summary>
    /// <param name="bump">A delegate that receives the DreamerInsightStats instance and modifies one or more counters.</param>
    private void BumpDreamer(Action<DreamerInsightStats> bump)
    {
        lock (_lock)
        {
            _data.Dreamer ??= new DreamerInsightStats();
            bump(_data.Dreamer!);
        }
    }

    // ── Retrieval ──

    /// <summary>Get the full insights snapshot.</summary>
    public InsightsData GetInsights()
    {
        lock (_lock) { return _data; }
    }

    /// <summary>Get summary string for display.</summary>
    public string GetSummary()
    {
        lock (_lock)
        {
            return $"Tokens: {_data.TotalInputTokens + _data.TotalOutputTokens:N0} " +
                   $"({_data.TotalInputTokens:N0} in / {_data.TotalOutputTokens:N0} out)\n" +
                   $"Turns: {_data.TotalTurns:N0} | Sessions: {_data.TotalSessions:N0}\n" +
                   $"Est. Cost: ${_data.EstimatedCostUsd:F4}\n" +
                   $"Tools: {_data.ToolUsage.Count} unique, {_data.ToolUsage.Values.Sum(t => t.TotalCalls):N0} total calls";
        }
    }

    // ── Persistence ──

    public void Save()
    {
        lock (_lock)
        {
            var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            File.WriteAllText(_dataPath, json);
        }
    }

    private InsightsData Load()
    {
        if (!File.Exists(_dataPath)) return new InsightsData();
        try
        {
            var json = File.ReadAllText(_dataPath);
            return JsonSerializer.Deserialize<InsightsData>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }) ?? new InsightsData();
        }
        catch (Exception ex)
        {
            BestEffort.LogFailure(_logger, ex, "loading insights data", $"path={_dataPath}");
            return new InsightsData();
        }
    }

    // ── Cost estimation (rough per-1M tokens) ──

    private static double EstimateCost(string? model, int inputTokens, int outputTokens)
    {
        var m = model?.ToLowerInvariant() ?? "";
        // Rough pricing per 1M tokens (input, output)
        var (inputPer1M, outputPer1M) = m switch
        {
            _ when m.Contains("gpt-4") => (30.0, 60.0),
            _ when m.Contains("gpt-3.5") => (0.5, 1.5),
            _ when m.Contains("claude-3-opus") => (15.0, 75.0),
            _ when m.Contains("claude-3-sonnet") => (3.0, 15.0),
            _ when m.Contains("claude-3-haiku") => (0.25, 1.25),
            _ when m.Contains("ollama") || m.Contains("local") => (0.0, 0.0),
            _ => (1.0, 2.0) // Generic fallback
        };

        return (inputTokens * inputPer1M / 1_000_000) + (outputTokens * outputPer1M / 1_000_000);
    }
}

// ── Data Models ──

public sealed class InsightsData
{
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public long TotalTurns { get; set; }
    public long TotalSessions { get; set; }
    public double EstimatedCostUsd { get; set; }
    public Dictionary<string, ModelStats> TokensByModel { get; set; } = new();
    public Dictionary<string, ToolStats> ToolUsage { get; set; } = new();
    public Dictionary<string, DailyStats> DailyTokens { get; set; } = new();
    public Dictionary<string, int> SessionsByPlatform { get; set; } = new();
    /// <summary>Optional counters for the Dreamer background worker.</summary>
    public DreamerInsightStats? Dreamer { get; set; }
}

public sealed class DreamerInsightStats
{
    public long Walks { get; set; }
    public long Digests { get; set; }
    public long Builds { get; set; }
    public long Signals { get; set; }
    public long StartupFailures { get; set; }
    public DateTimeOffset? LastStartupFailureUtc { get; set; }
    public string? LastStartupFailureMessage { get; set; }
}

public sealed class ModelStats
{
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long Turns { get; set; }
    public double EstimatedCostUsd { get; set; }
}

public sealed class ToolStats
{
    public long TotalCalls { get; set; }
    public long Successes { get; set; }
    public long Failures { get; set; }
    public long TotalDurationMs { get; set; }
    public double AvgDurationMs => TotalCalls > 0 ? (double)TotalDurationMs / TotalCalls : 0;
}

public sealed class DailyStats
{
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long Turns { get; set; }
}
