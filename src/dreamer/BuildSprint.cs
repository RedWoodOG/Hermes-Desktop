namespace Hermes.Agent.Dreamer;

using Microsoft.Extensions.Logging;

/// <summary>Autonomous build trigger — sandboxed under dreamer/projects/{slug}/.</summary>
public sealed class BuildSprint
{
    private readonly DreamerRoom _room;
    private readonly ILogger<BuildSprint> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="BuildSprint"/> that scaffolds sandbox workspaces under the room's ProjectsDir.
    /// </summary>
    /// <param name="room">Provides the base projects directory used to create per-project sandboxes.</param>
    /// <param name="logger">Logger used to record scaffold completion and related informational messages.</param>
    public BuildSprint(DreamerRoom room, ILogger<BuildSprint> logger)
    {
        _room = room;
        _logger = logger;
    }

    /// <summary>
    /// Scaffolds a sandbox project workspace under the room's projects directory and seeds initial documentation files.
    /// Full <c>Agent</c> tool execution is deferred; autonomy controls how much scaffolding is written.
    /// </summary>
    /// <param name="slug">Project slug used to name the workspace; the value is sanitized and validated. An invalid or dangerous slug causes an <see cref="ArgumentException"/>.</param>
    /// <param name="walkExcerpt">Seed text placed in the README's "Seed intent" section; the value is truncated to at most 8000 characters.</param>
    /// <param name="autonomy">Autonomy mode string included in the README. If not equal to "ideas" (case-insensitive), a SPRINT.md checklist is also created.</param>
    /// <param name="ct">Cancellation token for the directory and file write operations.</param>
    /// <returns>A task that completes after the workspace directory has been created and the README (and conditionally SPRINT.md) have been written.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="slug"/> is null, empty, or considered dangerous after sanitization.</exception>
    public async Task RunAsync(string slug, string walkExcerpt, string autonomy, CancellationToken ct)
    {
        var normalized = DreamerProjectSlug.Normalize(slug);
        if (normalized.Length == 0)
        {
            _logger.LogWarning(
                "Skipping Dreamer build sprint for invalid normalized slug {Slug} from input {InputSlug}",
                normalized,
                slug);
            return;
        }

        var dir = Path.Combine(_room.ProjectsDir, normalized);
        try
        {
            Directory.CreateDirectory(dir);

            var readme = $"""
                # Dreamer build: {normalized}

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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverableFileException(ex))
        {
            _logger.LogWarning(ex, "Dreamer build sprint scaffold failed for {Slug}", normalized);
        }
    }

    private static bool IsRecoverableFileException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException;
}
