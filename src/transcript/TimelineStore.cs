namespace Hermes.Agent.Transcript;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Durable runtime timeline for threads, turns, turn items, and replay events.
/// Kept separate from transcript JSONL so existing session listing stays clean.
/// </summary>
public sealed class TimelineStore
{
    private const int CurrentSchemaVersion = 1;

    private readonly string _threadsDir;
    private readonly string _turnsDir;
    private readonly string _itemsDir;
    private readonly string _eventsDir;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public TimelineStore(string timelineRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timelineRoot);

        _threadsDir = Path.Combine(timelineRoot, "threads");
        _turnsDir = Path.Combine(timelineRoot, "turns");
        _itemsDir = Path.Combine(timelineRoot, "items");
        _eventsDir = Path.Combine(timelineRoot, "events");

        Directory.CreateDirectory(_threadsDir);
        Directory.CreateDirectory(_turnsDir);
        Directory.CreateDirectory(_itemsDir);
        Directory.CreateDirectory(_eventsDir);
    }

    public async Task<ThreadRecord> GetOrCreateThreadAsync(
        string sessionId,
        string platform,
        string? workspaceRoot,
        string? provider,
        string? model,
        string? title,
        CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            return await GetOrCreateThreadNoLockAsync(sessionId, platform, workspaceRoot, provider, model, title, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<TurnRecord> StartTurnAsync(
        string sessionId,
        string platform,
        string inputSummary,
        string? workspaceRoot,
        string? provider,
        string? model,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputSummary);

        await _writeLock.WaitAsync(ct);
        try
        {
            var thread = await GetOrCreateThreadNoLockAsync(sessionId, platform, workspaceRoot, provider, model, inputSummary, ct);
            var now = DateTime.UtcNow;
            var turn = new TurnRecord
            {
                SchemaVersion = CurrentSchemaVersion,
                TurnId = NewId("turn"),
                ThreadId = thread.ThreadId,
                Sequence = NextTurnSequenceNoLock(thread.ThreadId),
                Status = TurnStatus.InProgress,
                StartedAt = now,
                InputSummary = Summarize(inputSummary)
            };

            await WriteJsonAtomicAsync(GetTurnPath(turn.TurnId), turn, ct);
            await WriteJsonAtomicAsync(GetThreadPath(thread.ThreadId), thread with
            {
                UpdatedAt = now,
                LatestTurnId = turn.TurnId,
                Title = string.IsNullOrWhiteSpace(thread.Title) ? turn.InputSummary : thread.Title
            }, ct);
            await AppendEventNoLockAsync(thread.ThreadId, turn.TurnId, null, "turn_started", null, ct);
            return turn;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<TurnItemRecord> AppendItemAsync(
        string threadId,
        string turnId,
        TurnItemKind kind,
        TurnItemStatus status,
        string contentSummary,
        string? role = null,
        int? messageIndex = null,
        string? messageRef = null,
        string? toolCallId = null,
        string? toolName = null,
        string? artifactPath = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var turn = await ReadJsonAsync<TurnRecord>(GetTurnPath(turnId), ct)
                ?? throw new InvalidOperationException($"Turn '{turnId}' does not exist.");

            var item = new TurnItemRecord
            {
                SchemaVersion = CurrentSchemaVersion,
                ItemId = NewId("item"),
                TurnId = turnId,
                ThreadId = threadId,
                Sequence = turn.ItemIds.Count + 1,
                Kind = kind,
                Role = role,
                ContentSummary = Summarize(contentSummary),
                MessageIndex = messageIndex,
                MessageRef = messageRef,
                ToolCallId = toolCallId,
                ToolName = toolName,
                Status = status,
                ArtifactPath = artifactPath,
                Metadata = metadata is null ? new Dictionary<string, string>() : new Dictionary<string, string>(metadata),
                CreatedAt = DateTime.UtcNow
            };

            var itemIds = turn.ItemIds.ToList();
            itemIds.Add(item.ItemId);

            await WriteJsonAtomicAsync(GetItemPath(item.ItemId), item, ct);
            await WriteJsonAtomicAsync(GetTurnPath(turnId), turn with { ItemIds = itemIds }, ct);
            await TouchThreadNoLockAsync(threadId, ct);
            await AppendEventNoLockAsync(threadId, turnId, item.ItemId, "item_appended", new Dictionary<string, string>
            {
                ["kind"] = kind.ToString(),
                ["status"] = status.ToString()
            }, ct);

            return item;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task CompleteTurnAsync(string threadId, string turnId, TurnStatus status, string? error, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var path = GetTurnPath(turnId);
            var turn = await ReadJsonAsync<TurnRecord>(path, ct);
            if (turn is null)
                return;

            var completedAt = DateTime.UtcNow;
            await WriteJsonAtomicAsync(path, turn with
            {
                Status = status,
                CompletedAt = completedAt,
                DurationMs = (long)(completedAt - turn.StartedAt).TotalMilliseconds,
                Error = string.IsNullOrWhiteSpace(error) ? null : error
            }, ct);

            await TouchThreadNoLockAsync(threadId, ct, turnId);
            await AppendEventNoLockAsync(threadId, turnId, null, "turn_completed", new Dictionary<string, string>
            {
                ["status"] = status.ToString()
            }, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<ThreadRecord?> LoadThreadAsync(string threadId, CancellationToken ct) =>
        await ReadJsonAsync<ThreadRecord>(GetThreadPath(threadId), ct);

    public async Task<TurnRecord?> LoadTurnAsync(string turnId, CancellationToken ct) =>
        await ReadJsonAsync<TurnRecord>(GetTurnPath(turnId), ct);

    public async Task<TurnRecord?> LoadLatestTurnForThreadAsync(string threadId, CancellationToken ct)
    {
        var thread = await LoadThreadAsync(threadId, ct);
        if (thread?.LatestTurnId is null)
            return null;

        return await LoadTurnAsync(thread.LatestTurnId, ct);
    }

    public async Task<IReadOnlyList<TurnItemRecord>> LoadTurnItemsAsync(string turnId, CancellationToken ct)
    {
        var turn = await LoadTurnAsync(turnId, ct);
        if (turn is null)
            return Array.Empty<TurnItemRecord>();

        var items = new List<TurnItemRecord>();
        foreach (var itemId in turn.ItemIds)
        {
            var item = await ReadJsonAsync<TurnItemRecord>(GetItemPath(itemId), ct);
            if (item is not null)
                items.Add(item);
        }

        return items;
    }

    public async Task<IReadOnlyList<ThreadRecord>> ListThreadsAsync(CancellationToken ct)
    {
        var threads = new List<ThreadRecord>();
        foreach (var path in Directory.EnumerateFiles(_threadsDir, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            var thread = await ReadJsonAsync<ThreadRecord>(path, ct);
            if (thread is not null)
                threads.Add(thread);
        }

        return threads.OrderByDescending(thread => thread.UpdatedAt).ToList();
    }

    public async Task<IReadOnlyList<RuntimeEventRecord>> LoadEventsAsync(string threadId, CancellationToken ct)
    {
        var path = GetEventPath(threadId);
        if (!File.Exists(path))
            return Array.Empty<RuntimeEventRecord>();

        var events = new List<RuntimeEventRecord>();
        var lines = await File.ReadAllLinesAsync(path, ct);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var evt = JsonSerializer.Deserialize<RuntimeEventRecord>(line, JsonOptions);
                if (evt is not null)
                    events.Add(evt);
            }
            catch (JsonException)
            {
                // A corrupt event line should not make the whole timeline unreadable.
            }
        }

        return events.OrderBy(evt => evt.EventSequence).ToList();
    }

    private async Task<ThreadRecord> GetOrCreateThreadNoLockAsync(
        string sessionId,
        string platform,
        string? workspaceRoot,
        string? provider,
        string? model,
        string? title,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        var path = GetThreadPath(sessionId);
        var existing = await ReadJsonAsync<ThreadRecord>(path, ct);
        if (existing is not null)
            return existing;

        var now = DateTime.UtcNow;
        var thread = new ThreadRecord
        {
            SchemaVersion = CurrentSchemaVersion,
            ThreadId = sessionId,
            SessionId = sessionId,
            Platform = platform,
            WorkspaceRoot = workspaceRoot,
            Provider = provider,
            Model = model,
            Title = Summarize(title),
            CreatedAt = now,
            UpdatedAt = now
        };

        await WriteJsonAtomicAsync(path, thread, ct);
        await AppendEventNoLockAsync(thread.ThreadId, null, null, "thread_created", null, ct);
        return thread;
    }

    private async Task TouchThreadNoLockAsync(string threadId, CancellationToken ct, string? latestTurnId = null)
    {
        var path = GetThreadPath(threadId);
        var thread = await ReadJsonAsync<ThreadRecord>(path, ct);
        if (thread is null)
            return;

        await WriteJsonAtomicAsync(path, thread with
        {
            UpdatedAt = DateTime.UtcNow,
            LatestTurnId = latestTurnId ?? thread.LatestTurnId
        }, ct);
    }

    private int NextTurnSequenceNoLock(string threadId)
    {
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(_turnsDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("threadId", out var value) &&
                    string.Equals(value.GetString(), threadId, StringComparison.Ordinal))
                {
                    count++;
                }
            }
            catch (JsonException)
            {
            }
        }

        return count + 1;
    }

    private async Task AppendEventNoLockAsync(
        string threadId,
        string? turnId,
        string? itemId,
        string eventType,
        IReadOnlyDictionary<string, string>? payload,
        CancellationToken ct)
    {
        var path = GetEventPath(threadId);
        var evt = new RuntimeEventRecord
        {
            SchemaVersion = CurrentSchemaVersion,
            EventSequence = NextEventSequenceNoLock(path),
            ThreadId = threadId,
            TurnId = turnId,
            ItemId = itemId,
            EventType = eventType,
            Timestamp = DateTime.UtcNow,
            Payload = payload is null ? new Dictionary<string, string>() : new Dictionary<string, string>(payload)
        };

        var json = JsonSerializer.Serialize(evt, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough | FileOptions.SequentialScan);
        await fs.WriteAsync(bytes, ct);
        await fs.FlushAsync(ct);
    }

    private static long NextEventSequenceNoLock(string path)
    {
        if (!File.Exists(path))
            return 1;

        var count = 0L;
        foreach (var line in File.ReadLines(path))
        {
            if (!string.IsNullOrWhiteSpace(line))
                count++;
        }

        return count + 1;
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return default;

        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return await JsonSerializer.DeserializeAsync<T>(fs, JsonOptions, ct);
    }

    private static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(fs, value, JsonOptions, ct);
                await fs.FlushAsync(ct);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private string GetThreadPath(string threadId) => Path.Combine(_threadsDir, $"{SafeFileName(threadId)}.json");
    private string GetTurnPath(string turnId) => Path.Combine(_turnsDir, $"{SafeFileName(turnId)}.json");
    private string GetItemPath(string itemId) => Path.Combine(_itemsDir, $"{SafeFileName(itemId)}.json");
    private string GetEventPath(string threadId) => Path.Combine(_eventsDir, $"{SafeFileName(threadId)}.jsonl");

    private static string NewId(string prefix)
    {
        var id = $"{prefix}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}";
        return id.Length <= 48 ? id : id[..48];
    }

    private static string Summarize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 240 ? normalized : normalized[..237] + "...";
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        return builder.ToString();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };
}
