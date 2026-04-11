namespace Hermes.Agent.Dreamer;

using Microsoft.Extensions.Logging;

/// <summary>Autonomous build trigger — sandboxed under dreamer/projects/{slug}/.</summary>
public sealed class BuildSprint
{
    private readonly DreamerRoom _room;
    private readonly ILogger<BuildSprint> _logger;

    public BuildSprint(DreamerRoom room, ILogger<BuildSprint> logger)
    {
        _room = room;
        _logger = logger;
    }

    /// <summary>
    /// Creates project workspace and seeds documentation. Full <c>Agent</c> tool execution is deferred;
    /// autonomy controls how much scaffolding is written.
    /// </summary>
    public async Task RunAsync(string slug, string walkExcerpt, string autonomy, CancellationToken ct)
    {
        var sanitized = SanitizeSlug(slug);
        if (string.IsNullOrEmpty(sanitized))
            throw new ArgumentException("Invalid or dangerous project slug", nameof(slug));

        var dir = Path.Combine(_room.ProjectsDir, sanitized);
        Directory.CreateDirectory(dir);

        var readme = $"""
            # Dreamer build: {slug}

            **Autonomy mode:** {autonomy}

            This directory is a **sandbox**. Nothing here is merged into the main Hermes tree until you promote it manually.

            ## Seed intent (from walk)
            {walkExcerpt[..Math.Min(8000, walkExcerpt.Length)]}

            ## Next steps
            - `ideas`: keep as notes only.
            - `drafts`: expand SPRINT.md with a checklist (no code execution).
            - `full`: reserved for future automated agent runs with tools pinned to this directory.
            """;

        await File.WriteAllTextAsync(Path.Combine(dir, "README.md"), readme, ct);

        if (!string.Equals(autonomy, "ideas", StringComparison.OrdinalIgnoreCase))
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "SPRINT.md"),
                "## Checklist\n\n- [ ] Clarify scope\n- [ ] Prototype\n- [ ] Tests\n",
                ct);
        }

        _logger.LogInformation("Dreamer build sprint scaffolded at {Dir}", dir);
    }

    /// <summary>Sanitize slug to prevent path traversal attacks.</summary>
    private static string SanitizeSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return "";

        // Remove dangerous path sequences
        var result = slug.Replace("..", "")
                         .Replace("/", "-")
                         .Replace("\\", "-");

        // Remove invalid filename chars
        foreach (var c in Path.GetInvalidFileNameChars())
            result = result.Replace(c.ToString(), "");

        // Ensure not rooted
        if (Path.IsPathRooted(result))
            return "";

        return result.Trim();
    }
}