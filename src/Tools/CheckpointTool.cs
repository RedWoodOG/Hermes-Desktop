namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;

/// <summary>
/// Create/restore filesystem snapshots for safe rollback.
///
/// New snapshots are written to <c>&lt;parent-of-source&gt;/&lt;basename-of-source&gt;-checkpoints/&lt;name&gt;/</c>
/// so the snapshot location is structurally outside the source directory and cannot
/// recurse back into it (issue #52).
///
/// The legacy <c>checkpointDir</c> passed to the constructor remains a fallback location
/// for <c>list</c> and <c>restore</c>, so checkpoints created before this change are still
/// reachable.
/// </summary>
public sealed class CheckpointTool : ITool
{
    private readonly string _checkpointDir;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public string Name => "checkpoint";
    public string Description => "Create, restore, or list filesystem snapshots for safe rollback.";
    public Type ParametersType => typeof(CheckpointParameters);

    public CheckpointTool(string checkpointDir)
    {
        _checkpointDir = Path.GetFullPath(checkpointDir).TrimEnd(Path.DirectorySeparatorChar);
        Directory.CreateDirectory(_checkpointDir);
    }

    public Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (CheckpointParameters)parameters;

        return p.Action?.ToLowerInvariant() switch
        {
            "create" => CreateCheckpointAsync(p.Directory, p.Name, ct),
            "restore" => RestoreCheckpointAsync(p.Directory, p.Name, ct),
            "list" => ListCheckpointsAsync(p.Directory, ct),
            _ => Task.FromResult(ToolResult.Fail($"Unknown action: {p.Action}. Use create, restore, or list."))
        };
    }

    private Task<ToolResult> CreateCheckpointAsync(string? directory, string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return Task.FromResult(ToolResult.Fail("Directory is required for create."));

        if (!System.IO.Directory.Exists(directory))
            return Task.FromResult(ToolResult.Fail($"Directory not found: {directory}"));

        try
        {
            var dirFull = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            var snapshotRoot = ComputeSnapshotRoot(dirFull);

            if (string.Equals(dirFull, snapshotRoot, PathComparison))
                return Task.FromResult(ToolResult.Fail(
                    $"Refusing to checkpoint a directory into itself: {dirFull}"));

            Directory.CreateDirectory(snapshotRoot);

            var snapshotName = string.IsNullOrWhiteSpace(name)
                ? $"checkpoint_{DateTime.UtcNow:yyyyMMdd_HHmmss}"
                : name;
            var snapshotPath = Path.Combine(snapshotRoot, snapshotName);

            CopyDirectory(dirFull, snapshotPath, excludePrefixes: new[] { snapshotRoot, _checkpointDir });

            return Task.FromResult(ToolResult.Ok($"Checkpoint created: {snapshotName} -> {snapshotPath}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Failed to create checkpoint: {ex.Message}", ex));
        }
    }

    private Task<ToolResult> RestoreCheckpointAsync(string? directory, string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(ToolResult.Fail("Name is required for restore."));

        if (string.IsNullOrWhiteSpace(directory))
            return Task.FromResult(ToolResult.Fail("Directory is required for restore."));

        var dirFull = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        var perSource = Path.Combine(ComputeSnapshotRoot(dirFull), name);
        var legacy = Path.Combine(_checkpointDir, name);

        var snapshotPath = System.IO.Directory.Exists(perSource) ? perSource
                         : System.IO.Directory.Exists(legacy)    ? legacy
                         : null;

        if (snapshotPath is null)
            return Task.FromResult(ToolResult.Fail($"Checkpoint not found: {name}"));

        try
        {
            CopyDirectory(snapshotPath, dirFull);
            return Task.FromResult(ToolResult.Ok($"Checkpoint restored: {name} -> {dirFull}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Failed to restore checkpoint: {ex.Message}", ex));
        }
    }

    private Task<ToolResult> ListCheckpointsAsync(string? directory, CancellationToken ct)
    {
        var entries = new List<string>();

        if (!string.IsNullOrWhiteSpace(directory))
        {
            var dirFull = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            entries.AddRange(EnumerateCheckpoints(ComputeSnapshotRoot(dirFull), label: "(source)"));
        }

        entries.AddRange(EnumerateCheckpoints(_checkpointDir, label: "(legacy)"));

        if (entries.Count == 0)
            return Task.FromResult(ToolResult.Ok("No checkpoints found."));

        return Task.FromResult(ToolResult.Ok(string.Join("\n", entries)));
    }

    private static IEnumerable<string> EnumerateCheckpoints(string root, string label)
    {
        if (!System.IO.Directory.Exists(root))
            return Array.Empty<string>();

        return System.IO.Directory.GetDirectories(root)
            .Select(d => new DirectoryInfo(d))
            .OrderByDescending(d => d.CreationTimeUtc)
            .Select(d => $"{d.Name}  {label}  (created {d.CreationTimeUtc:yyyy-MM-dd HH:mm:ss} UTC)");
    }

    private static string ComputeSnapshotRoot(string directoryFullPath)
    {
        var parent = System.IO.Directory.GetParent(directoryFullPath)?.FullName ?? directoryFullPath;
        var basename = Path.GetFileName(directoryFullPath);
        if (string.IsNullOrEmpty(basename)) basename = "root";
        return Path.Combine(parent, $"{basename}-checkpoints").TrimEnd(Path.DirectorySeparatorChar);
    }

    private static void CopyDirectory(string sourceDir, string destDir, string[]? excludePrefixes = null)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in System.IO.Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var subDir in System.IO.Directory.GetDirectories(sourceDir))
        {
            try
            {
                if ((File.GetAttributes(subDir) & FileAttributes.ReparsePoint) != 0)
                    continue;

                var subFull = Path.GetFullPath(subDir).TrimEnd(Path.DirectorySeparatorChar);
                if (excludePrefixes is not null && IsUnderAny(subFull, excludePrefixes))
                    continue;

                var destSubDir = Path.Combine(destDir, Path.GetFileName(subDir));
                CopyDirectory(subDir, destSubDir, excludePrefixes);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsUnderAny(string subFull, string[] prefixes)
    {
        foreach (var p in prefixes)
        {
            if (string.IsNullOrEmpty(p)) continue;
            var pNorm = p.TrimEnd(Path.DirectorySeparatorChar);
            if (subFull.Equals(pNorm, PathComparison)) return true;
            if (subFull.StartsWith(pNorm + Path.DirectorySeparatorChar, PathComparison)) return true;
        }
        return false;
    }
}

public sealed class CheckpointParameters
{
    public required string Action { get; init; }
    public string? Name { get; init; }
    public string? Directory { get; init; }
}
