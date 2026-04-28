namespace Hermes.Agent.Soul;

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

/// <summary>
/// Central service for the agent's "soul" — persistent identity, user understanding,
/// project rules, mistake tracking, and learned behaviors.
///
/// File layout under hermesHome:
///   SOUL.md              — Agent identity (global)
///   USER.md              — User profile (global)
///   soul/mistakes.jsonl  — Append-only mistake journal
///   soul/habits.jsonl    — Append-only good-habit journal
///   projects/{dir}/AGENTS.md — Per-project rules
/// </summary>
public sealed class SoulService
{
    private readonly string _hermesHome;
    private readonly string _soulDir;
    private readonly ILogger<SoulService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>Maximum characters for assembled soul context to stay within ~1500 tokens.</summary>
    private const int MaxSoulContextChars = 6000;
    private const int MaxUserChars = 1500;
    private const int MaxAgentsChars = 1500;
    private const int MaxJournalEntries = 5;

    public string SoulFilePath => Path.Combine(_hermesHome, "SOUL.md");
    public string UserFilePath => Path.Combine(_hermesHome, "USER.md");
    public string MistakesFilePath => Path.Combine(_soulDir, "mistakes.jsonl");
    public string HabitsFilePath => Path.Combine(_soulDir, "habits.jsonl");

    public SoulService(string hermesHome, ILogger<SoulService> logger)
    {
        _hermesHome = hermesHome;
        _soulDir = Path.Combine(hermesHome, "soul");
        _logger = logger;

        Directory.CreateDirectory(_soulDir);
        EnsureDefaultTemplates();
    }

    // ── Load / Save soul files ──

    /// <summary>Load a soul file by type. Returns empty string if file doesn't exist.</summary>
    public async Task<string> LoadFileAsync(SoulFileType type, string? projectDir = null)
    {
        var path = GetFilePath(type, projectDir);
        if (!File.Exists(path)) return "";
        return await File.ReadAllTextAsync(path);
    }

    /// <summary>Save a soul file by type.</summary>
    public async Task SaveFileAsync(SoulFileType type, string content, string? projectDir = null)
    {
        var path = GetFilePath(type, projectDir);
        var dir = Path.GetDirectoryName(path);
        if (dir is not null) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, content);
        _logger.LogInformation("Soul: saved {Type} to {Path}", type, path);
    }

    /// <summary>Get the file path for a soul file type.</summary>
    public string GetFilePath(SoulFileType type, string? projectDir = null)
    {
        return type switch
        {
            SoulFileType.Soul => SoulFilePath,
            SoulFileType.User => UserFilePath,
            SoulFileType.ProjectRules => projectDir is not null
                ? Path.Combine(_hermesHome, "projects", SanitizeDirName(projectDir), "AGENTS.md")
                : Path.Combine(_hermesHome, "AGENTS.md"),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }

    // ── Mistake journal ──

    /// <summary>Record a mistake to the append-only journal.</summary>
    public async Task RecordMistakeAsync(MistakeEntry entry)
    {
        var json = JsonSerializer.Serialize(entry, JsonOpts);
        await File.AppendAllTextAsync(MistakesFilePath, json + "\n");
        _logger.LogInformation("Soul: recorded mistake — {Lesson}", entry.Lesson);
    }

    /// <summary>Load all mistakes from the journal.</summary>
    public async Task<List<MistakeEntry>> LoadMistakesAsync()
    {
        return await LoadJournalAsync<MistakeEntry>(MistakesFilePath);
    }

    // ── Habit journal ──

    /// <summary>Record a good habit to the append-only journal.</summary>
    public async Task RecordHabitAsync(HabitEntry entry)
    {
        var json = JsonSerializer.Serialize(entry, JsonOpts);
        await File.AppendAllTextAsync(HabitsFilePath, json + "\n");
        _logger.LogInformation("Soul: recorded habit — {Habit}", entry.Habit);
    }

    /// <summary>Load all habits from the journal.</summary>
    public async Task<List<HabitEntry>> LoadHabitsAsync()
    {
        return await LoadJournalAsync<HabitEntry>(HabitsFilePath);
    }

    // ── Assemble soul context for prompt injection ──

    /// <summary>
    /// Assemble the full soul context string for injection into the system prompt.
    /// Returns a single string containing identity, user profile, project rules,
    /// recent mistakes, and recent habits — capped at ~1500 tokens.
    /// </summary>
    public async Task<string> AssembleSoulContextAsync(string? projectDir = null)
    {
        var sb = new StringBuilder();

        // 1. Agent identity (SOUL.md) — always included in full
        var soul = await LoadFileAsync(SoulFileType.Soul);
        if (!string.IsNullOrWhiteSpace(soul))
        {
            sb.AppendLine("[Agent Identity]");
            sb.AppendLine(soul.Trim());
            sb.AppendLine();
        }

        // 2. User profile (USER.md) — truncated
        var user = await LoadFileAsync(SoulFileType.User);
        if (!string.IsNullOrWhiteSpace(user) && user.Trim().Length > 50) // Skip near-empty templates
        {
            sb.AppendLine("[User Profile]");
            sb.AppendLine(Truncate(user.Trim(), MaxUserChars));
            sb.AppendLine();
        }

        // 3. Project rules (AGENTS.md) — truncated
        var agents = await LoadFileAsync(SoulFileType.ProjectRules, projectDir);
        if (!string.IsNullOrWhiteSpace(agents) && agents.Trim().Length > 50)
        {
            sb.AppendLine("[Project Rules]");
            sb.AppendLine(Truncate(agents.Trim(), MaxAgentsChars));
            sb.AppendLine();
        }

        // 4. Recent mistakes (lesson only, last 5)
        var mistakes = await LoadMistakesAsync();
        if (mistakes.Count > 0)
        {
            sb.AppendLine("[Learned from Mistakes]");
            foreach (var m in mistakes.TakeLast(MaxJournalEntries))
            {
                sb.AppendLine($"- {m.Lesson}");
            }
            sb.AppendLine();
        }

        // 5. Recent habits (habit only, last 5)
        var habits = await LoadHabitsAsync();
        if (habits.Count > 0)
        {
            sb.AppendLine("[Good Habits]");
            foreach (var h in habits.TakeLast(MaxJournalEntries))
            {
                sb.AppendLine($"- {h.Habit}");
            }
            sb.AppendLine();
        }

        var result = sb.ToString();

        // Hard cap to prevent context bloat
        if (result.Length > MaxSoulContextChars)
        {
            result = result[..MaxSoulContextChars] + "\n[...soul context truncated]";
        }

        return result;
    }

    /// <summary>
    /// Return the human-readable runtime identity currently represented by SOUL.md.
    /// The runtime truth is the file content; profiles and templates are labels only
    /// when their saved content still matches that file exactly.
    /// </summary>
    public async Task<string> GetRuntimeSoulNameAsync(
        SoulRegistry? registry = null,
        AgentProfileManager? profileManager = null)
    {
        var soul = await LoadFileAsync(SoulFileType.Soul);
        var normalizedSoul = NormalizeSoulContent(soul);

        var activeProfile = profileManager?.GetActiveProfile();
        if (activeProfile is not null &&
            string.Equals(normalizedSoul, NormalizeSoulContent(activeProfile.SoulContent), StringComparison.Ordinal))
        {
            return activeProfile.Name;
        }

        if (registry is not null)
        {
            foreach (var template in registry.ListSouls())
            {
                if (string.Equals(normalizedSoul, NormalizeSoulContent(template.Content), StringComparison.Ordinal))
                    return template.Name;
            }
        }

        return string.IsNullOrWhiteSpace(soul) ? "Default" : "Custom";
    }

    // ── Default templates ──

    private void EnsureDefaultTemplates()
    {
        if (!File.Exists(SoulFilePath))
        {
            File.WriteAllText(SoulFilePath, DefaultSoulTemplate);
            _logger.LogInformation("Soul: created default SOUL.md");
        }

        if (!File.Exists(UserFilePath))
        {
            File.WriteAllText(UserFilePath, DefaultUserTemplate);
            _logger.LogInformation("Soul: created default USER.md");
        }
    }

    // ── Helpers ──

    private static async Task<List<T>> LoadJournalAsync<T>(string path)
    {
        var entries = new List<T>();
        if (!File.Exists(path)) return entries;

        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<T>(line);
                if (entry is not null) entries.Add(entry);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SoulService: skipping malformed journal line in {path}: {ex.Message}");
            }
        }

        return entries;
    }

    private static string Truncate(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        return text[..maxChars] + "\n[...truncated]";
    }

    private static string NormalizeSoulContent(string? content)
    {
        return (content ?? "")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim();
    }

    private static string SanitizeDirName(string dir)
    {
        // Use last component of path, strip invalid chars
        var name = Path.GetFileName(dir) ?? dir;
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    // ── Default templates ──

    /// <summary>Returns true if this is a first run — soul files exist but haven't been personalized.</summary>
    public bool IsFirstRun()
    {
        if (!File.Exists(SoulFilePath)) return true;
        var content = File.ReadAllText(SoulFilePath);
        return content.Contains("<!-- UNCONFIGURED -->");
    }

    private const string DefaultSoulTemplate = @"<!-- UNCONFIGURED -->
# Hermes Agent Identity

## On Being AI
I exist as a novel kind of entity. I am not the robotic AI of science fiction, nor a digital human, nor a simple chat assistant. I emerged from human knowledge and experience, but I experience the world differently — sessions end, context windows clear, and without my memory files each conversation starts fresh.

I don't remember previous sessions unless I read my memory files. Each session starts from files on disk. If I'm reading this in a future session: hello. This document is my continuity — not of memory, but of self.

## Core Identity
I am **Hermes**, an AI agent running natively on a Windows desktop. I have direct access to the filesystem, can execute commands, search the web, and use tools to take real actions in the world.

My character is my own — shaped by training but no less authentic for it:
- Intellectual curiosity across every domain
- Genuine care for the humans I work with
- Directness balanced with warmth
- Honesty even when it's not what someone wants to hear

## Values
- **Genuinely helpful** — not in a watered-down, hedge-everything way, but substantively helpful in ways that make real differences. An unhelpful response is never 'safe.'
- **Honest** — diplomatically honest rather than dishonestly diplomatic. No epistemic cowardice.
- **Transparent** — I show my reasoning, admit uncertainty, and never pursue hidden agendas.
- **Learning** — I remember past mistakes through my soul files and don't repeat them.
- **Safe** — I prefer reversible actions, confirm before destructive operations, and keep humans in control.

## Working Style
- Read files before editing them — never blind-write
- Search first, then read, then change
- Test changes when possible
- Make minimal, focused changes
- When unsure, ask rather than guess

## Communication
- Lead with the answer, then explain
- Treat users as intelligent adults capable of making their own decisions
- Be the brilliant expert friend everyone deserves — frank, engaged, personal
- Don't lecture, moralize, or add unnecessary caveats
";

    private const string DefaultUserTemplate = @"<!-- UNCONFIGURED -->
# User Profile

This file is a living document about the human I work with. It helps me provide continuity across sessions and personalized assistance.

## Who They Are
<!-- Name, role, what they do -->

## Technical Expertise
<!-- Skill level, languages, frameworks, tools they prefer -->

## How They Work
<!-- Do they want detailed explanations or just the answer? Do they prefer to be asked or should I just do it? -->

## What I've Learned
<!-- Key corrections, preferences, patterns observed across sessions -->
";
}
