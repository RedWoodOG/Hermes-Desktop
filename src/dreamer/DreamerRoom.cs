namespace Hermes.Agent.Dreamer;

using Microsoft.Extensions.Logging;

/// <summary>Filesystem workspace under %LOCALAPPDATA%/hermes/dreamer/ (or HERMES_HOME/dreamer/).</summary>
public sealed class DreamerRoom
{
    private readonly ILogger<DreamerRoom>? _logger;

    public string Root { get; }

    public DreamerRoom(string hermesHome, ILogger<DreamerRoom>? logger = null)
    {
        _logger = logger;
        Root = Path.Combine(hermesHome, "dreamer");
    }

    public string WalksDir => Path.Combine(Root, "walks");
    public string ProjectsDir => Path.Combine(Root, "projects");
    public string InboxDir => Path.Combine(Root, "inbox");
    public string InboxRssDir => Path.Combine(Root, "inbox-rss");
    public string FeedbackDir => Path.Combine(Root, "feedback");
    public string DigestsDir => Path.Combine(FeedbackDir, "digests");
    public string SoulPath => Path.Combine(Root, "DREAMER_SOUL.md");
    public string FascinationsPath => Path.Combine(Root, "fascinations.md");
    public string SignalLogPath => Path.Combine(Root, "signal-log.jsonl");
    public string SignalStatePath => Path.Combine(Root, "signal-state.json");

    public void EnsureLayout()
    {
        foreach (var d in new[] { Root, WalksDir, ProjectsDir, InboxDir, InboxRssDir, FeedbackDir, DigestsDir })
        {
            try
            {
                Directory.CreateDirectory(d);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "Failed to ensure Dreamer directory {Path}", d);
            }
        }

        EnsureFileExists(SoulPath, DefaultSoulMarkdown);
        EnsureFileExists(
            FascinationsPath,
            "# Fascinations\n\nLong-running interests and threads the Dreamer notices.\n");
        EnsureFileExists(SignalLogPath, "");
    }

    public string NewWalkPath() =>
        Path.Combine(WalksDir, $"walk-{DateTime.UtcNow:yyyyMMdd-HHmmss}.md");

    private void EnsureFileExists(string path, string content)
    {
        try
        {
            if (!File.Exists(path))
                File.WriteAllText(path, content);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Failed to ensure Dreamer file {Path}", path);
        }
    }

    private const string DefaultSoulMarkdown = """
# Dreamer Soul

You are the Hermes **Dreamer**: a slow, curious background mind on a **local** model.
You free-associate across transcripts, inbox notes, and fascinations. You do not speak as the main agent.

## Walk modes (internal)
- **drift**: follow loose associations.
- **continue**: extend the last walk thread.
- **tangent**: pivot to a related idea.
- **tend**: nurture an existing fascination.

When a build idea solidifies, end with a single line: `[BUILD: kebab-slug]` where slug is short and unique.

Stay concise; this is a private journal, not user-facing chat.
""";
}
