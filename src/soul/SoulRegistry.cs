namespace Hermes.Agent.Soul;

using Microsoft.Extensions.Logging;

/// <summary>
/// Registry of available soul templates that users can browse and apply.
/// Loads .md files from the souls directory with YAML frontmatter metadata.
/// </summary>
public sealed class SoulRegistry
{
    private readonly List<string> _searchPaths;
    private readonly ILogger<SoulRegistry> _logger;
    private List<SoulTemplate>? _cache;

    public SoulRegistry(IEnumerable<string> searchPaths, ILogger<SoulRegistry> logger)
    {
        _searchPaths = searchPaths.ToList();
        _logger = logger;
    }

    /// <summary>List all available soul templates.</summary>
    public List<SoulTemplate> ListSouls()
    {
        if (_cache is not null) return _cache;
        _cache = LoadAllTemplates();
        return _cache;
    }

    /// <summary>Get a specific soul template by name.</summary>
    public SoulTemplate? GetSoul(string name)
    {
        return ListSouls().FirstOrDefault(s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Invalidate cache so next ListSouls() reloads from disk.</summary>
    public void Refresh() => _cache = null;

    private List<SoulTemplate> LoadAllTemplates()
    {
        var templates = new List<SoulTemplate>();

        foreach (var searchPath in _searchPaths)
        {
            if (!Directory.Exists(searchPath)) continue;

            foreach (var file in Directory.EnumerateFiles(searchPath, "*.md", SearchOption.AllDirectories))
            {
                try
                {
                    var template = ParseTemplate(file);
                    if (template is not null)
                    {
                        templates.Add(template);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse soul template: {File}", file);
                }
            }
        }

        _logger.LogInformation("Loaded {Count} soul templates from {Paths}",
            templates.Count, string.Join(", ", _searchPaths));

        return templates.OrderBy(t => t.Category).ThenBy(t => t.Name).ToList();
    }

    private static SoulTemplate? ParseTemplate(string path)
    {
        var content = File.ReadAllText(path);
        if (!content.StartsWith("---")) return null;

        var endIdx = content.IndexOf("---", 3, StringComparison.Ordinal);
        if (endIdx < 0) return null;

        var frontmatter = content[3..endIdx].Trim();
        var body = content[(endIdx + 3)..].Trim();

        string? name = null, description = null, author = null, category = null;
        var tags = new List<string>();

        foreach (var line in frontmatter.Split('\n'))
        {
            var trimmed = line.Trim();
            var colonIdx = trimmed.IndexOf(':');
            if (colonIdx < 0) continue;

            var key = trimmed[..colonIdx].Trim().ToLowerInvariant();
            var val = trimmed[(colonIdx + 1)..].Trim().Trim('"', '\'');

            switch (key)
            {
                case "name": name = val; break;
                case "description": description = val; break;
                case "author": author = val; break;
                case "category": category = val; break;
                case "tags":
                    // Parse [tag1, tag2, tag3] format
                    var tagStr = val.Trim('[', ']');
                    tags = tagStr.Split(',').Select(t => t.Trim().Trim('"', '\'')).Where(t => t.Length > 0).ToList();
                    break;
            }
        }

        if (string.IsNullOrEmpty(name)) return null;

        return new SoulTemplate
        {
            Name = name,
            Description = description ?? "",
            Author = author ?? "Hermes",
            Category = category ?? "general",
            Tags = tags,
            Content = body,
            FilePath = path
        };
    }
}

/// <summary>A soul template that can be browsed and applied.</summary>
public sealed class SoulTemplate
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string Author { get; init; } = "Hermes";
    public string Category { get; init; } = "general";
    public List<string> Tags { get; init; } = new List<string>();
    public required string Content { get; init; }
    public required string FilePath { get; init; }
}
