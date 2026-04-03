namespace Hermes.Agent.Dream;

using Hermes.Agent.LLM;
using Hermes.Agent.Core;
using Hermes.Agent.Transcript;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using System.Text.RegularExpressions;

/// <summary>
/// AutoDream - Background memory consolidation system.
/// Periodically scans session transcripts and consolidates learnings into persistent memory.
/// Runs every 10 minutes when enabled.
/// </summary>

public sealed class AutoDreamService : BackgroundService
{
    private static readonly TimeSpan SCAN_INTERVAL = TimeSpan.FromMinutes(10);
    private readonly ILogger<AutoDreamService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _memoryDir;
    private readonly IChatClient _chatClient;
    private readonly TranscriptStore _transcriptStore;
    private DateTime _lastConsolidation = DateTime.MinValue;

    public AutoDreamService(
        ILogger<AutoDreamService> logger,
        ILoggerFactory loggerFactory,
        string memoryDir,
        IChatClient chatClient,
        TranscriptStore transcriptStore)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _memoryDir = memoryDir;
        _chatClient = chatClient;
        _transcriptStore = transcriptStore;
        Directory.CreateDirectory(memoryDir);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("AutoDream service starting with {Interval} interval", SCAN_INTERVAL);

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(SCAN_INTERVAL, ct);

            if (!IsAutoDreamEnabled())
            {
                _logger.LogDebug("AutoDream disabled, skipping");
                continue;
            }

            try
            {
                await ConsolidateSessionsAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dream consolidation failed");
            }
        }
    }

    private bool IsAutoDreamEnabled()
    {
        var config = DreamConfig.Load();

        if (!config.Enabled) return false;

        var sessionCount = GetSessionCount();
        if (sessionCount < config.MinSessions)
        {
            _logger.LogDebug("AutoDream: {Count} sessions < {Min} minimum", sessionCount, config.MinSessions);
            return false;
        }

        var hoursSince = (DateTime.UtcNow - _lastConsolidation).TotalHours;
        if (hoursSince < config.MinHours)
        {
            _logger.LogDebug("AutoDream: {Hours:F1}h since last < {Min}h minimum", hoursSince, config.MinHours);
            return false;
        }

        return true;
    }

    private async Task ConsolidateSessionsAsync(CancellationToken ct)
    {
        var sessions = await FindSessionsSinceLastConsolidationAsync(ct);

        if (sessions.Count == 0)
        {
            _logger.LogDebug("No new sessions to consolidate");
            return;
        }

        _logger.LogInformation("Consolidating {Count} sessions", sessions.Count);

        var consolidator = new ConsolidationAgent(
            _chatClient, _memoryDir, _transcriptStore,
            _loggerFactory.CreateLogger<ConsolidationAgent>());

        await consolidator.ConsolidateAsync(sessions, ct);
        _lastConsolidation = DateTime.UtcNow;

        _logger.LogInformation("Dream consolidation complete");
    }

    private async Task<List<DreamSession>> FindSessionsSinceLastConsolidationAsync(CancellationToken ct)
    {
        var allIds = _transcriptStore.GetAllSessionIds();
        var sessions = new List<DreamSession>();

        foreach (var id in allIds)
        {
            var messages = await _transcriptStore.LoadSessionAsync(id, ct);
            if (messages.Count == 0) continue;

            var firstTimestamp = messages[0].Timestamp;
            if (firstTimestamp > _lastConsolidation)
            {
                sessions.Add(new DreamSession { Id = id, Messages = messages });
            }
        }

        return sessions;
    }

    private int GetSessionCount() => _transcriptStore.GetAllSessionIds().Count;
}

/// <summary>
/// Consolidation Agent - Forked agent that consolidates learnings into memory.
/// Uses 4-phase prompt: Orient → Gather → Consolidate → Prune
/// </summary>
public sealed class ConsolidationAgent
{
    private readonly IChatClient _chatClient;
    private readonly string _memoryDir;
    private readonly TranscriptStore _transcriptStore;
    private readonly ILogger<ConsolidationAgent> _logger;

    public ConsolidationAgent(IChatClient chatClient, string memoryDir, TranscriptStore transcriptStore, ILogger<ConsolidationAgent> logger)
    {
        _chatClient = chatClient;
        _memoryDir = memoryDir;
        _transcriptStore = transcriptStore;
        _logger = logger;
    }

    public async Task ConsolidateAsync(List<DreamSession> sessions, CancellationToken ct)
    {
        var existingMemories = LoadExistingMemories();
        var transcripts = sessions.Select(s => FormatTranscript(s)).ToList();
        var prompt = BuildConsolidationPrompt(existingMemories, transcripts);

        var response = await _chatClient.CompleteAsync(
            [new Message { Role = "user", Content = prompt }], ct);

        await ApplyConsolidationChangesAsync(response, ct);
    }

    private List<MemoryContext> LoadExistingMemories()
    {
        var memories = new List<MemoryContext>();
        if (!Directory.Exists(_memoryDir)) return memories;

        foreach (var file in Directory.EnumerateFiles(_memoryDir, "*.md"))
        {
            try
            {
                var content = File.ReadAllText(file);
                var filename = Path.GetFileName(file);

                // Parse YAML frontmatter
                string? type = null;
                string? name = null;
                string? description = null;
                var body = content;

                if (content.StartsWith("---"))
                {
                    var endIdx = content.IndexOf("---", 3);
                    if (endIdx > 0)
                    {
                        var frontmatter = content[3..endIdx].Trim();
                        body = content[(endIdx + 3)..].Trim();

                        foreach (var line in frontmatter.Split('\n'))
                        {
                            var parts = line.Split(':', 2);
                            if (parts.Length != 2) continue;
                            var key = parts[0].Trim().ToLowerInvariant();
                            var val = parts[1].Trim();
                            switch (key)
                            {
                                case "name": name = val; break;
                                case "type": type = val; break;
                                case "description": description = val; break;
                            }
                        }
                    }
                }

                memories.Add(new MemoryContext
                {
                    Path = file,
                    Filename = filename,
                    Content = body,
                    Type = type,
                    Name = name,
                    Description = description
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load memory file {File}", file);
            }
        }

        return memories;
    }

    private static string FormatTranscript(DreamSession session)
    {
        const int MaxChars = 4000;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## Session: {session.Id}");

        // Take messages from the end if too many (preserve recent context)
        var messages = session.Messages;
        foreach (var msg in messages)
        {
            if (msg.Role == "tool") continue; // Skip tool results for brevity
            sb.AppendLine($"### {msg.Role}");
            sb.AppendLine(msg.Content.Length > 500 ? msg.Content[..500] + "..." : msg.Content);
            sb.AppendLine();

            if (sb.Length > MaxChars) break;
        }

        return sb.Length > MaxChars ? sb.ToString()[..MaxChars] + "\n[...truncated]" : sb.ToString();
    }

    private string BuildConsolidationPrompt(List<MemoryContext> existing, List<string> transcripts)
    {
        var memorySection = existing.Count > 0
            ? string.Join("\n", existing.Take(10).Select(m =>
                $"- **{m.Filename}** ({m.Type ?? "unknown"}): {(m.Content.Length > 200 ? m.Content[..200] + "..." : m.Content)}"))
            : "(no existing memories)";

        return $@"You are a memory consolidation agent. Your job is to extract lasting learnings from recent conversations and update the project's memory system.

# Phase 1: Orient — Current Memories
{memorySection}

# Phase 2: Gather Recent Signal
{string.Join("\n---\n", transcripts.Take(5))}

# Phase 3: Consolidate
Extract new learnings. Focus on:
- Decisions made and their reasoning
- User preferences and feedback
- Architecture patterns discovered
- Tool usage patterns
- Project context (goals, deadlines, blockers)

Ignore: transient errors, debug output, failed attempts, temporary workarounds.

# Phase 4: Prune and Index
Flag any existing memories that are now outdated or contradicted.

Return your changes in EXACTLY this format:

## New Memories
For each new memory:
```
FILENAME: descriptive_name.md
TYPE: user|feedback|project|reference
DESCRIPTION: one-line description
CONTENT: the memory content
```

## Updated Memories
For each update:
```
FILENAME: existing_file.md
CONTENT: the updated content
```

## Deleted Memories
List filenames to remove, one per line.

## Summary
Brief summary of what was consolidated.";
    }

    private async Task ApplyConsolidationChangesAsync(string response, CancellationToken ct)
    {
        var changeCount = 0;

        // Parse New Memories
        var newSection = ExtractSection(response, "## New Memories", "## ");
        if (newSection is not null)
        {
            var blocks = Regex.Split(newSection, @"(?=FILENAME:)").Where(b => b.Trim().Length > 0);
            foreach (var block in blocks)
            {
                var filename = ExtractField(block, "FILENAME");
                var type = ExtractField(block, "TYPE");
                var description = ExtractField(block, "DESCRIPTION");
                var content = ExtractField(block, "CONTENT");

                if (filename is null || content is null) continue;

                var frontmatter = $"---\nname: {filename.Replace(".md", "")}\ndescription: {description ?? "auto-consolidated"}\ntype: {type ?? "project"}\n---\n\n";
                var path = Path.Combine(_memoryDir, filename);
                await File.WriteAllTextAsync(path, frontmatter + content, ct);
                changeCount++;
                _logger.LogInformation("Dream: created memory {Filename}", filename);
            }
        }

        // Parse Updated Memories
        var updateSection = ExtractSection(response, "## Updated Memories", "## ");
        if (updateSection is not null)
        {
            var blocks = Regex.Split(updateSection, @"(?=FILENAME:)").Where(b => b.Trim().Length > 0);
            foreach (var block in blocks)
            {
                var filename = ExtractField(block, "FILENAME");
                var content = ExtractField(block, "CONTENT");
                if (filename is null || content is null) continue;

                var path = Path.Combine(_memoryDir, filename);
                if (File.Exists(path))
                {
                    // Preserve frontmatter, update body
                    var existing = await File.ReadAllTextAsync(path, ct);
                    var endIdx = existing.IndexOf("---", 3);
                    var frontmatter = endIdx > 0 ? existing[..(endIdx + 3)] + "\n\n" : "";
                    await File.WriteAllTextAsync(path, frontmatter + content, ct);
                    changeCount++;
                    _logger.LogInformation("Dream: updated memory {Filename}", filename);
                }
            }
        }

        // Parse Deleted Memories
        var deleteSection = ExtractSection(response, "## Deleted Memories", "## ");
        if (deleteSection is not null)
        {
            foreach (var line in deleteSection.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var filename = line.TrimStart('-', ' ');
                if (string.IsNullOrWhiteSpace(filename)) continue;
                var path = Path.Combine(_memoryDir, filename);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    changeCount++;
                    _logger.LogInformation("Dream: deleted memory {Filename}", filename);
                }
            }
        }

        _logger.LogInformation("Dream consolidation applied {Count} changes", changeCount);
    }

    private static string? ExtractSection(string text, string header, string nextHeaderPrefix)
    {
        var start = text.IndexOf(header);
        if (start < 0) return null;
        start += header.Length;

        var end = text.IndexOf(nextHeaderPrefix, start);
        // Find the next header that isn't our own
        while (end >= 0 && end == start)
            end = text.IndexOf(nextHeaderPrefix, end + 1);

        return end > 0 ? text[start..end].Trim() : text[start..].Trim();
    }

    private static string? ExtractField(string block, string fieldName)
    {
        var pattern = $@"{fieldName}:\s*(.+?)(?:\n[A-Z]+:|$)";
        var match = Regex.Match(block, pattern, RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
}

/// <summary>Dream configuration.</summary>
public sealed class DreamConfig
{
    public bool Enabled { get; set; } = true;
    public int MinSessions { get; set; } = 3;
    public double MinHours { get; set; } = 0.5;

    public static DreamConfig Load()
    {
        // Check environment variables first
        var enabled = Environment.GetEnvironmentVariable("HERMES_DREAM_ENABLED");
        var minSessions = Environment.GetEnvironmentVariable("HERMES_DREAM_MIN_SESSIONS");
        var minHours = Environment.GetEnvironmentVariable("HERMES_DREAM_MIN_HOURS");

        var config = new DreamConfig();
        if (enabled is not null) config.Enabled = enabled != "0" && enabled.ToLowerInvariant() != "false";
        if (minSessions is not null && int.TryParse(minSessions, out var ms)) config.MinSessions = ms;
        if (minHours is not null && double.TryParse(minHours, out var mh)) config.MinHours = mh;

        return config;
    }
}

public sealed class MemoryContext
{
    public required string Path { get; init; }
    public required string Filename { get; init; }
    public required string Content { get; init; }
    public string? Type { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
}

public sealed class DreamSession
{
    public required string Id { get; init; }
    public List<Message> Messages { get; init; } = [];
}
