namespace Hermes.Agent.Transcript;

using System.Collections.Concurrent;
using Hermes.Agent.Core;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Transcript-first persistence layer.
/// Every message is written to disk BEFORE updating in-memory state.
/// Crash-proof, resume-capable, JSONL format.
/// </summary>
public sealed class TranscriptStore
{
    private readonly string _transcriptsDir;
    private readonly ConcurrentDictionary<string, List<Message>> _cache = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly bool _eagerFlush;
    
    public TranscriptStore(string transcriptsDir, bool eagerFlush = false)
    {
        _transcriptsDir = transcriptsDir;
        _eagerFlush = eagerFlush || Environment.GetEnvironmentVariable("HERMES_EAGER_FLUSH") != null;
        
        Directory.CreateDirectory(transcriptsDir);
    }
    
    /// <summary>
    /// CRITICAL: Save message to disk BEFORE updating in-memory state.
    /// This is what makes Hermes crash-proof.
    /// </summary>
    public async Task SaveMessageAsync(string sessionId, Message message, CancellationToken ct)
    {
        var transcriptPath = GetTranscriptPath(sessionId);

        // Ensure directory exists
        Directory.CreateDirectory(Path.GetDirectoryName(transcriptPath)!);

        // Serialize to JSONL
        var json = JsonSerializer.Serialize(message, JsonOptions);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json + "\n");

        // INV-008: Atomic transcript writes using FileStream with WriteThrough
        // Ensures data hits disk immediately, preventing data loss on crash.
        await _writeLock.WaitAsync(ct);
        try
        {
            using var fs = new FileStream(
                transcriptPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.WriteThrough | FileOptions.SequentialScan);
            await fs.WriteAsync(bytes, ct);
            await fs.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
        
        // NOW update in-memory cache
        _cache.AddOrUpdate(sessionId, 
            _ => new List<Message> { message },
            (_, list) => { list.Add(message); return list; });
    }
    
    /// <summary>
    /// Load entire session transcript from disk.
    /// </summary>
    public async Task<List<Message>> LoadSessionAsync(string sessionId, CancellationToken ct)
    {
        // Check cache first
        if (_cache.TryGetValue(sessionId, out var cached))
            return cached.ToList();
        
        var transcriptPath = GetTranscriptPath(sessionId);
        
        if (!File.Exists(transcriptPath))
            throw new SessionNotFoundException(sessionId);
        
        var lines = await File.ReadAllLinesAsync(transcriptPath, ct);
        
        var messages = new List<Message>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            
            var message = JsonSerializer.Deserialize<Message>(line, JsonOptions);
            if (message != null)
                messages.Add(message);
        }
        
        // Cache for next time
        _cache[sessionId] = messages;
        
        return messages;
    }
    
    /// <summary>
    /// Check if session exists (has transcript).
    /// </summary>
    public bool SessionExists(string sessionId)
    {
        if (_cache.ContainsKey(sessionId))
            return true;
        
        var transcriptPath = GetTranscriptPath(sessionId);
        return File.Exists(transcriptPath);
    }
    
    /// <summary>
    /// Get all session IDs (from disk + cache).
    /// </summary>
    public List<string> GetAllSessionIds()
    {
        var fromCache = _cache.Keys.ToList();
        var fromDisk = Directory.EnumerateFiles(_transcriptsDir, "*.jsonl")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Where(id => !fromCache.Contains(id))
            .ToList();
        
        fromCache.AddRange(fromDisk);
        return fromCache;
    }
    
    /// <summary>
    /// Delete session transcript.
    /// </summary>
    public async Task DeleteSessionAsync(string sessionId, CancellationToken ct)
    {
        var transcriptPath = GetTranscriptPath(sessionId);
        var activityPath = GetActivityPath(sessionId);

        if (File.Exists(transcriptPath) || File.Exists(activityPath))
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                if (File.Exists(transcriptPath))
                    File.Delete(transcriptPath);
                if (File.Exists(activityPath))
                    File.Delete(activityPath);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        _cache.TryRemove(sessionId, out _);
    }
    
    /// <summary>
    /// Clear in-memory cache (free memory, keep disk).
    /// </summary>
    public void ClearCache()
    {
        _cache.Clear();
    }

    /// <summary>
    /// Append an activity entry to the session's activity JSONL file.
    /// </summary>
    public async Task SaveActivityAsync(string sessionId, ActivityEntry entry, CancellationToken ct)
    {
        var activityPath = GetActivityPath(sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(activityPath)!);

        var json = JsonSerializer.Serialize(entry, ActivityJsonOptions);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json + "\n");

        // INV-008: Atomic writes with WriteThrough for activity log too
        await _writeLock.WaitAsync(ct);
        try
        {
            using var fs = new FileStream(
                activityPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.WriteThrough | FileOptions.SequentialScan);
            await fs.WriteAsync(bytes, ct);
            await fs.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Load all activity entries for a session from its activity JSONL file.
    /// </summary>
    public async Task<List<ActivityEntry>> LoadActivityAsync(string sessionId, CancellationToken ct)
    {
        var activityPath = GetActivityPath(sessionId);
        if (!File.Exists(activityPath))
            return new List<ActivityEntry>();

        var lines = await File.ReadAllLinesAsync(activityPath, ct);
        var entries = new List<ActivityEntry>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var entry = JsonSerializer.Deserialize<ActivityEntry>(line, ActivityJsonOptions);
            if (entry is not null)
                entries.Add(entry);
        }
        return entries;
    }
    
    private string GetTranscriptPath(string sessionId) =>
        Path.Combine(_transcriptsDir, $"{sessionId}.jsonl");

    private string GetActivityPath(string sessionId) =>
        Path.Combine(_transcriptsDir, $"{sessionId}.activity.jsonl");
    
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions ActivityJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };
}

/// <summary>
/// Session not found exception.
/// </summary>
public sealed class SessionNotFoundException : Exception
{
    public SessionNotFoundException(string sessionId) 
        : base($"Session '{sessionId}' not found. Use 'hermes list' to see available sessions.")
    {
    }
}

/// <summary>
/// Resume manager - loads sessions and restores state.
/// </summary>
public sealed class ResumeManager
{
    private readonly TranscriptStore _transcripts;
    private readonly ILogger<ResumeManager> _logger;
    
    public ResumeManager(TranscriptStore transcripts, ILogger<ResumeManager> logger)
    {
        _transcripts = transcripts;
        _logger = logger;
    }
    
    /// <summary>
    /// Resume a session from transcript.
    /// Returns session with full message history.
    /// </summary>
    public async Task<Session> ResumeSessionAsync(string sessionId, CancellationToken ct)
    {
        _logger.LogInformation("Resuming session {SessionId}", sessionId);
        
        // Load transcript
        var messages = await _transcripts.LoadSessionAsync(sessionId, ct);
        
        if (messages.Count == 0)
        {
            throw new InvalidOperationException($"Session {sessionId} is empty");
        }
        
        // Restore session state
        var session = new Session
        {
            Id = sessionId,
            Messages = messages,
            UserId = messages.FirstOrDefault(m => m.Role == "user")?.ToolCallId, // Hacky, fix later
            Platform = "cli",
            CreatedAt = messages.First().Timestamp,
            LastActivityAt = messages.Last().Timestamp
        };
        
        _logger.LogInformation(
            "Resumed session {SessionId} with {Count} messages, last activity {LastActivity}",
            sessionId, 
            messages.Count, 
            session.LastActivityAt);
        
        Console.WriteLine($"Resumed session {sessionId} ({messages.Count} messages)");
        
        return session;
    }
    
    /// <summary>
    /// List all available sessions with metadata.
    /// </summary>
    public async Task<List<SessionSummary>> ListSessionsAsync(CancellationToken ct)
    {
        var sessionIds = _transcripts.GetAllSessionIds();
        var summaries = new List<SessionSummary>();
        
        foreach (var id in sessionIds.OrderByDescending(id => id)) // Newest first
        {
            try
            {
                var messages = await _transcripts.LoadSessionAsync(id, ct);
                
                summaries.Add(new SessionSummary
                {
                    SessionId = id,
                    MessageCount = messages.Count,
                    CreatedAt = messages.FirstOrDefault()?.Timestamp ?? DateTime.MinValue,
                    LastActivityAt = messages.LastOrDefault()?.Timestamp ?? DateTime.MinValue,
                    FirstMessage = messages.FirstOrDefault(m => m.Role == "user")?.Content?.Take(50).ToString() ?? ""
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load session {SessionId}", id);
            }
        }
        
        return summaries.OrderByDescending(s => s.LastActivityAt).ToList();
    }
}

public sealed class SessionSummary
{
    public required string SessionId { get; init; }
    public int MessageCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime LastActivityAt { get; init; }
    public required string FirstMessage { get; init; }
}

/// <summary>
/// Session history for up-arrow navigation and Ctrl+R search.
/// Separate from transcripts - tracks commands/prompts only.
/// </summary>
public sealed class SessionHistory
{
    private readonly string _historyPath;
    private readonly List<HistoryEntry> _pendingEntries = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private System.Timers.Timer? _flushTimer;
    private const int MAX_HISTORY_ITEMS = 100;
    private const int MAX_PASTED_CONTENT_LENGTH = 1024;
    
    public SessionHistory(string historyPath)
    {
        _historyPath = historyPath;
        
        // Auto-flush after 100ms of inactivity
        _flushTimer = new System.Timers.Timer(100);
        _flushTimer.Elapsed += async (_, _) => await FlushAsync(CancellationToken.None);
        _flushTimer.AutoReset = false;
    }
    
    /// <summary>
    /// Add entry to history (fire-and-forget flush).
    /// </summary>
    public void AddToHistory(string command, string project, string? sessionId)
    {
        var entry = new HistoryEntry
        {
            Command = command,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Project = project,
            SessionId = sessionId
        };
        
        _pendingEntries.Add(entry);
        
        // Reset flush timer
        _flushTimer?.Stop();
        _flushTimer?.Start();
    }
    
    /// <summary>
    /// Get history entries (current session first, then from file).
    /// </summary>
    public async IAsyncEnumerable<HistoryEntry> GetHistoryAsync()
    {
        // Current session pending entries first (newest first)
        foreach (var entry in _pendingEntries.OrderByDescending(e => e.Timestamp))
        {
            yield return entry;
        }
        
        // Then from file
        if (File.Exists(_historyPath))
        {
            var lines = await File.ReadAllLinesAsync(_historyPath);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                
                var entry = JsonSerializer.Deserialize<HistoryEntry>(line);
                if (entry != null)
                    yield return entry;
            }
        }
    }
    
    /// <summary>
    /// Remove last entry (undo).
    /// </summary>
    public async Task RemoveLastAsync(CancellationToken ct)
    {
        // Fast path: if still in pending, splice out
        if (_pendingEntries.Count > 0)
        {
            _pendingEntries.RemoveAt(_pendingEntries.Count - 1);
            return;
        }
        
        // Slow path: remove from file
        if (!File.Exists(_historyPath))
            return;
        
        await _flushLock.WaitAsync(ct);
        try
        {
            var lines = (await File.ReadAllLinesAsync(_historyPath, ct)).ToList();
            if (lines.Count > 0)
            {
                lines.RemoveAt(lines.Count - 1);
                await File.WriteAllLinesAsync(_historyPath, lines, ct);
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }
    
    /// <summary>
    /// Clear all history.
    /// </summary>
    public async Task ClearAsync(CancellationToken ct)
    {
        _pendingEntries.Clear();
        
        if (File.Exists(_historyPath))
        {
            await _flushLock.WaitAsync(ct);
            try
            {
                File.Delete(_historyPath);
            }
            finally
            {
                _flushLock.Release();
            }
        }
    }
    
    private async Task FlushAsync(CancellationToken ct)
    {
        if (_pendingEntries.Count == 0)
            return;
        
        await _flushLock.WaitAsync(ct);
        try
        {
            var jsonLines = _pendingEntries.Select(e => JsonSerializer.Serialize(e));
            await File.AppendAllLinesAsync(_historyPath, jsonLines, ct);
            _pendingEntries.Clear();
        }
        finally
        {
            _flushLock.Release();
        }
    }
}

public sealed class HistoryEntry
{
    public required string Command { get; init; }
    public long Timestamp { get; init; }
    public required string Project { get; init; }
    public string? SessionId { get; init; }
}
