namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;

/// <summary>
/// File reading tool with offset/limit support.
/// Supports text files, images, PDFs, and Jupyter notebooks.
/// </summary>
public sealed class ReadFileTool : ITool
{
    public string Name => "read_file";
    public string Description => "Read files from the local filesystem with optional offset/limit for large files";
    public Type ParametersType => typeof(ReadFileParameters);
    
    public Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (ReadFileParameters)parameters;
        var resolved = ResolvePath(p.FilePath, out var rejection);
        if (rejection is not null)
            return Task.FromResult(ToolResult.Fail(rejection));
        return ReadFileAsync(resolved, p.Offset, p.Limit, ct);
    }

    /// <summary>
    /// Resolve a tool-supplied path. Absolute paths are honored as-is; relative
    /// paths are resolved against <c>HERMES_DESKTOP_WORKSPACE</c> when set so the
    /// model can reference workspace files without knowing the absolute prefix
    /// (the v2.4.0 regression: relative paths silently bound to the process CWD,
    /// which on Windows installers is typically <c>C:\Windows\System32</c>).
    /// Falls back to <see cref="Directory.GetCurrentDirectory"/> when the env
    /// var is unset, preserving prior behavior for users who haven't opted in.
    ///
    /// Strict mode: when <c>HERMES_DESKTOP_WORKSPACE_STRICT=1</c> AND a workspace
    /// is configured, the resolved path MUST live inside the workspace tree.
    /// This blocks both absolute escapes (<c>C:\Windows\System32\...</c>) and
    /// relative-traversal escapes (<c>../../etc/passwd</c>). Disabled by default
    /// because the agent legitimately reads system files (config samples, bundled
    /// assets) — opting in is a deliberate security posture for shared/managed
    /// installs and CI runners.
    /// </summary>
    /// <param name="filePath">Path supplied by the tool caller.</param>
    /// <param name="rejection">When non-null, the call MUST be rejected with this
    /// human-readable reason. Returned via out-param rather than thrown so the
    /// tool can surface the policy violation as a normal failed result.</param>
    private static string ResolvePath(string filePath, out string? rejection)
    {
        rejection = null;
        if (string.IsNullOrWhiteSpace(filePath)) return filePath;

        var workspace = Environment.GetEnvironmentVariable("HERMES_DESKTOP_WORKSPACE");
        var hasWorkspace = !string.IsNullOrWhiteSpace(workspace) && Directory.Exists(workspace);
        var baseDir = hasWorkspace ? workspace! : Directory.GetCurrentDirectory();

        // Path.GetFullPath resolves ".." segments and normalizes separators.
        var resolved = Path.IsPathRooted(filePath)
            ? Path.GetFullPath(filePath)
            : Path.GetFullPath(Path.Combine(baseDir, filePath));

        if (hasWorkspace && IsStrictModeEnabled() && !IsInsideWorkspace(resolved, workspace!))
        {
            rejection = $"Path '{filePath}' resolves outside HERMES_DESKTOP_WORKSPACE; " +
                        "strict mode is enabled (HERMES_DESKTOP_WORKSPACE_STRICT=1).";
        }

        return resolved;
    }

    private static bool IsStrictModeEnabled()
    {
        // Accept "1" as the canonical opt-in. Anything else (including unset,
        // "0", "true", "false") leaves strict mode OFF — we deliberately do
        // NOT accept "true" because precedent across our tooling treats
        // numeric flags as the unambiguous machine-friendly form, and we'd
        // rather reject ambiguous configs at the env-var layer than guess.
        var raw = Environment.GetEnvironmentVariable("HERMES_DESKTOP_WORKSPACE_STRICT");
        return string.Equals(raw, "1", StringComparison.Ordinal);
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is inside or equal to the
    /// <paramref name="workspace"/> directory after full normalization.
    /// Uses a trailing-separator boundary check so a workspace
    /// <c>C:\work\proj</c> does not erroneously contain <c>C:\work\proj-evil</c>.
    /// </summary>
    private static bool IsInsideWorkspace(string candidate, string workspace)
    {
        var normWorkspace = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normCandidate = Path.GetFullPath(candidate);

        // Use case-insensitive compare on Windows (the only platform this app
        // ships to), case-sensitive elsewhere — matches NTFS semantics.
        var cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(normCandidate, normWorkspace, cmp))
            return true;

        var prefix = normWorkspace + Path.DirectorySeparatorChar;
        return normCandidate.StartsWith(prefix, cmp);
    }

    private Task<ToolResult> ReadFileAsync(string filePath, int? offset, int? limit, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return Task.FromResult(ToolResult.Fail($"File not found: {filePath}"));
            }
            
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            
            // Handle special file types
            if (extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp")
            {
                return Task.FromResult(ToolResult.Ok($"[IMAGE] {filePath} - Binary image file"));
            }
            
            if (extension == ".pdf")
            {
                return Task.FromResult(ToolResult.Ok($"[PDF] {filePath} - PDF file (text extraction not implemented)"));
            }
            
            if (extension == ".ipynb")
            {
                return ReadNotebookAsync(filePath, ct);
            }
            
            // Read text file
            var lines = File.ReadAllLines(filePath);
            
            // Apply offset/limit
            var start = offset ?? 0;
            var count = limit ?? lines.Length - start;
            var selectedLines = lines.Skip(start).Take(count).ToArray();
            
            // Format with line numbers (cat -n style)
            var output = string.Join("\n", selectedLines.Select((line, i) => $"{start + i + 1,6}: {line}"));

            // Record read timestamp for stale file detection
            FileReadTracker.RecordRead(filePath);

            return Task.FromResult(ToolResult.Ok(output));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Failed to read file: {ex.Message}", ex));
        }
    }
    
    private async Task<ToolResult> ReadNotebookAsync(string filePath, CancellationToken ct)
    {
        try
        {
            // Simple notebook reader - just extract code cells
            var content = await File.ReadAllTextAsync(filePath, ct);
            return ToolResult.Ok($"[NOTEBOOK] {filePath}\n\n{content}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to read notebook: {ex.Message}", ex);
        }
    }
}

public sealed class ReadFileParameters
{
    public required string FilePath { get; init; }
    public int? Offset { get; init; }
    public int? Limit { get; init; }
}
