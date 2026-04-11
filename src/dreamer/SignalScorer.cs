namespace Hermes.Agent.Dreamer;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

/// <summary>In-process signal extraction, scoring, decay, and trigger gates.</summary>
public sealed class SignalScorer
{
    private readonly DreamerRoom _room;
    private readonly ILogger<SignalScorer> _logger;
    private readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly JsonSerializerOptions _jsonLogOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SignalScorer(DreamerRoom room, ILogger<SignalScorer> logger)
    {
        _room = room;
        _logger = logger;
    }

    public SignalBoard LoadBoard()
    {
        if (!File.Exists(_room.SignalStatePath))
            return new SignalBoard();
        try
        {
            var json = File.ReadAllText(_room.SignalStatePath);
            return JsonSerializer.Deserialize<SignalBoard>(json, _json) ?? new SignalBoard();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load signal board; resetting");
            return new SignalBoard();
        }
    }

    public void SaveBoard(SignalBoard board)
    {
        var json = JsonSerializer.Serialize(board, _json);
        File.WriteAllText(_room.SignalStatePath, json);
    }

    public void AppendSignalLog(SignalEvent evt)
    {
        var line = JsonSerializer.Serialize(evt, _jsonLogOptions) + "\n";
        File.AppendAllText(_room.SignalLogPath, line);
    }

    /// <summary>Parse walk text for build slug and heuristic signals; update board with decay.</summary>
    public void ProcessWalk(
        string walkText,
        int echoScore,
        DreamerConfig config,
        out string? buildSlug)
    {
        buildSlug = null;
        var m = Regex.Match(walkText, @"\[BUILD:\s*([a-zA-Z0-9_-]+)\]", RegexOptions.IgnoreCase);
        if (m.Success)
            buildSlug = m.Groups[1].Value.ToLowerInvariant();

        var board = LoadBoard();
        ApplyDecay(board);

        void Add(string type, double weight, string? projectKey = null)
        {
            var key = projectKey ?? "general";
            if (!board.Projects.TryGetValue(key, out var ps))
            {
                ps = new ProjectSignals();
                board.Projects[key] = ps;
            }

            var echoFactor = (6.0 - echoScore) / 5.0;
            if (echoFactor < 0.2) echoFactor = 0.2;
            var delta = weight * echoFactor;
            ps.Score += delta;
            if (!ps.SignalTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
                ps.SignalTypes.Add(type);

            AppendSignalLog(new SignalEvent
            {
                Utc = DateTime.UtcNow,
                Type = type,
                ProjectKey = key,
                Delta = delta,
                EchoScore = echoScore
            });
        }

        if (Regex.IsMatch(walkText, @"\b(excited|fascinating|love|amazing|can't wait)\b", RegexOptions.IgnoreCase))
            Add("excitement", 2.0);
        if (Regex.IsMatch(walkText, @"\b(frustrat|blocked|annoyed|stuck|hate)\b", RegexOptions.IgnoreCase))
            Add("frustration", 1.5);
        if (Regex.IsMatch(walkText, @"\b(again|repeat|same idea|already)\b", RegexOptions.IgnoreCase))
            Add("return", 1.0);
        if (Regex.IsMatch(walkText, @"\b(commit|ship|implement|build)\b", RegexOptions.IgnoreCase))
            Add("commit", 3.0, buildSlug);
        if (Regex.IsMatch(walkText, @"\b(cool(ing)? down|never mind|forget)\b", RegexOptions.IgnoreCase))
            Add("cooling", -2.0);

        if (buildSlug is not null)
            Add("mention", 1.0, buildSlug);

        board.PositiveWalkStreak++;
        board.LastWalkUtc = DateTime.UtcNow;
        SaveBoard(board);
    }

    private static void ApplyDecay(SignalBoard board)
    {
        if (board.LastWalkUtc == default)
            return;

        var days = (DateTime.UtcNow - board.LastWalkUtc).TotalDays;
        if (days <= 0) return;

        var factor = Math.Max(0, 1.0 - days / 30.0);
        if (factor >= 0.999) return;

        foreach (var ps in board.Projects.Values)
            ps.Score *= factor;
    }

    /// <summary>True if a build sprint should start for the given slug.</summary>
    public bool ShouldTriggerBuild(string? slug, DreamerConfig config, out ProjectSignals? signals)
    {
        signals = null;
        if (string.IsNullOrWhiteSpace(slug))
            return false;

        var board = LoadBoard();
        if (board.PositiveWalkStreak < config.MinWalksToTrigger)
            return false;

        if (!board.Projects.TryGetValue(slug, out var ps))
            return false;

        signals = ps;
        if (ps.Score < config.TriggerThreshold)
            return false;

        var distinct = ps.SignalTypes.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (distinct < 2 && InferDistinctTypesFromLog(slug) < 2)
            return false;

        return true;
    }

    private int InferDistinctTypesFromLog(string slug)
    {
        if (!File.Exists(_room.SignalLogPath))
            return 0;

        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(_room.SignalLogPath).TakeLast(500))
        {
            try
            {
                var evt = JsonSerializer.Deserialize<SignalEvent>(line, _jsonLogOptions);
                if (evt is null || !string.Equals(evt.ProjectKey, slug, StringComparison.OrdinalIgnoreCase))
                    continue;
                types.Add(evt.Type);
            }
            catch { /* skip */ }
        }

        return types.Count;
    }

    public void ResetProjectAfterBuild(string slug)
    {
        var board = LoadBoard();
        board.Projects.Remove(slug);
        board.PositiveWalkStreak = 0;
        SaveBoard(board);
    }
}

public sealed class SignalBoard
{
    public Dictionary<string, ProjectSignals> Projects { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int PositiveWalkStreak { get; set; }
    public DateTime LastWalkUtc { get; set; }
}

public sealed class ProjectSignals
{
    public double Score { get; set; }
    public List<string> SignalTypes { get; set; } = new();
}

public sealed class SignalEvent
{
    public DateTime Utc { get; set; }
    public string Type { get; set; } = "";
    public string ProjectKey { get; set; } = "general";
    public double Delta { get; set; }
    public int EchoScore { get; set; }
}