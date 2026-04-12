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

    /// <summary>
    /// Initializes a new SignalScorer that persists and evaluates walk signals using the provided room storage and logger.
    /// </summary>
    /// <param name="room">Provides signal state and log file paths and room-scoped storage.</param>
    /// <param name="logger">Logger used for warnings and operational diagnostics.</param>
    public SignalScorer(DreamerRoom room, ILogger<SignalScorer> logger)
    {
        _room = room;
        _logger = logger;
    }

    /// <summary>
    /// Loads the persisted SignalBoard for the current room, or provides a new empty board when no usable state exists.
    /// </summary>
    /// <returns>The deserialized SignalBoard from the room's state file, or a new empty SignalBoard if the file is missing or cannot be read or parsed.</returns>
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

    /// <summary>
    /// Persists the provided SignalBoard to the room's signal state file.
    /// </summary>
    /// <param name="board">The SignalBoard to serialize and save as the current persistent state.</param>
    public void SaveBoard(SignalBoard board)
    {
        try
        {
            var json = JsonSerializer.Serialize(board, _json);
            File.WriteAllText(_room.SignalStatePath, json);
        }
        catch (Exception ex) when (IsRecoverableFileException(ex))
        {
            _logger.LogWarning(ex, "Failed to save signal board");
        }
    }

    /// <summary>
    /// Appends a SignalEvent as a single-line JSON entry to the room's signal log file.
    /// </summary>
    /// <param name="evt">The signal event to serialize and append; written as one-line (compact) JSON to the room's signal log path.</param>
    public void AppendSignalLog(SignalEvent evt)
    {
        try
        {
            var line = JsonSerializer.Serialize(evt, _jsonLogOptions) + "\n";
            File.AppendAllText(_room.SignalLogPath, line);
        }
        catch (Exception ex) when (IsRecoverableFileException(ex))
        {
            _logger.LogWarning(ex, "Failed to append Dreamer signal event for {ProjectKey}", evt.ProjectKey);
        }
    }

    /// <summary>
    /// Parse a walk transcript for an optional build slug and heuristic signals, apply time-based decay, update per-project scores and global streak, append signal events to the log, and persist the updated board.
    /// </summary>
    /// <param name="walkText">Text of the walk to analyze for signals and a possible build slug.</param>
    /// <param name="echoScore">Score used to compute an echo factor (clamped to a minimum of 0.2) that scales each signal's weight.</param>
    /// <param name="config">Configuration object (accepted but not consulted by this method).</param>
    /// <param name="buildSlug">Outputs the lowercased build slug if a `[BUILD: ...]` token is found in <paramref name="walkText"/>; otherwise `null`.</param>
    public void ProcessWalk(
        string walkText,
        int echoScore,
        DreamerConfig config,
        out string? buildSlug)
    {
        buildSlug = null;
        var m = Regex.Match(walkText, @"\[BUILD:\s*([a-zA-Z0-9_-]+)\]", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var candidate = m.Groups[1].Value;
            var normalized = DreamerProjectSlug.Normalize(candidate);
            if (normalized.Length > 0)
                buildSlug = normalized;
            else
                _logger.LogWarning(
                    "Ignoring Dreamer build marker with invalid normalized slug {Slug} from input {InputSlug}",
                    normalized,
                    candidate);
        }

        // Strip BUILD metadata to prevent false commit signal
        walkText = Regex.Replace(walkText, @"\[BUILD:\s*[a-zA-Z0-9_-]+\]", "", RegexOptions.IgnoreCase);

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

    /// <summary>
    /// Apply a time-based multiplicative decay to every project's score on the provided board.
    /// </summary>
    /// <param name="board">The SignalBoard whose ProjectSignals.Score values will be decayed in place based on the elapsed time since <see cref="SignalBoard.LastWalkUtc"/>.</param>
    /// <remarks>
    /// The decay factor is computed as max(0, 1.0 - daysElapsed / 30.0) and multiplied into each project's score; no changes are made if <see cref="SignalBoard.LastWalkUtc"/> is unset, the elapsed days are not positive, or the computed factor is effectively 1.0.
    /// </remarks>
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

    /// <summary>
    /// Determines whether a build sprint should be started for the specified project slug.
    /// </summary>
    /// <param name="slug">Project slug to evaluate; may be null or whitespace.</param>
    /// <param name="config">Configuration values used to evaluate triggers (e.g., MinWalksToTrigger, TriggerThreshold).</param>
    /// <param name="signals">If eligible, set to the project's <see cref="ProjectSignals"/>; otherwise null.</param>
    /// <returns>`true` if the slug meets streak, score, and distinct-signal requirements to trigger a build; `false` otherwise.</returns>
    public bool ShouldTriggerBuild(string? slug, DreamerConfig config, out ProjectSignals? signals)
    {
        signals = null;
        if (!DreamerProjectSlug.TryNormalize(slug, out var normalized))
            return false;

        var board = LoadBoard();
        if (board.PositiveWalkStreak < config.MinWalksToTrigger)
            return false;

        if (!board.Projects.TryGetValue(normalized, out var ps))
            return false;

        signals = ps;
        if (ps.Score < config.TriggerThreshold)
            return false;

        var distinct = ps.SignalTypes.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (distinct < 2 && InferDistinctTypesFromLog(normalized) < 2)
            return false;

        return true;
    }

    private int InferDistinctTypesFromLog(string normalizedSlug)
    {
        if (!File.Exists(_room.SignalLogPath))
            return 0;

        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var line in File.ReadLines(_room.SignalLogPath).TakeLast(500))
            {
                try
                {
                    var evt = JsonSerializer.Deserialize<SignalEvent>(line, _jsonLogOptions);
                    if (evt is null || !string.Equals(evt.ProjectKey, normalizedSlug, StringComparison.OrdinalIgnoreCase))
                        continue;
                    types.Add(evt.Type);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Skipping malformed signal log row while scoring {Slug}", normalizedSlug);
                }
            }
        }
        catch (Exception ex) when (IsRecoverableFileException(ex))
        {
            _logger.LogWarning(ex, "Failed to read Dreamer signal log for {Slug}", normalizedSlug);
        }

        return types.Count;
    }

    /// <summary>
    /// Clears persisted signal state for the specified project and resets the global positive-walk streak to zero.
    /// </summary>
    /// <param name="slug">The project slug or key whose saved signals should be removed.</param>
    public void ResetProjectAfterBuild(string slug)
    {
        if (!DreamerProjectSlug.TryNormalize(slug, out var normalized))
            return;

        var board = LoadBoard();
        board.Projects.Remove(normalized);
        board.PositiveWalkStreak = 0;
        SaveBoard(board);
    }

    private static bool IsRecoverableFileException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException;
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
