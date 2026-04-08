namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;

/// <summary>
/// Create/restore filesystem snapshots for safe rollback.
/// Copies directory contents to timestamped checkpoints.
/// </summary>
public sealed class CheckpointTool : ITool
{
    private readonly string _checkpointDir;

    public string Name => "checkpoint";
    public string Description => "Create, restore, or list filesystem snapshots for safe rollback.";
    public Type ParametersType => typeof(CheckpointParameters);

    public CheckpointTool(string checkpointDir)
    {
        _checkpointDir = checkpointDir;
        Directory.CreateDirectory(_checkpointDir);
    }

    public Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (CheckpointParameters)parameters;

        return p.Action?.ToLowerInvariant() switch
        {
            "create" => CreateCheckpointAsync(p.Directory, p.Name, ct),
            "restore" => RestoreCheckpointAsync(p.Directory, p.Name, ct),
            "list" => ListCheckpointsAsync(ct),
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
            var snapshotName = string.IsNullOrWhiteSpace(name)
                ? $"checkpoint_{DateTime.UtcNow:yyyyMMdd_HHmmss}"
                : name;
            var snapshotPath = Path.Combine(_checkpointDir, snapshotName);

            CopyDirectory(directory, snapshotPath);

            return Task.FromResult(ToolResult.Ok($"Checkpoint created: {snapshotName}"));
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

        var snapshotPath = Path.Combine(_checkpointDir, name);

        if (!System.IO.Directory.Exists(snapshotPath))
            return Task.FromResult(ToolResult.Fail($"Checkpoint not found: {name}"));

        try
        {
            CopyDirectory(snapshotPath, directory);
            return Task.FromResult(ToolResult.Ok($"Checkpoint restored: {name} -> {directory}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Failed to restore checkpoint: {ex.Message}", ex));
        }
    }

    private Task<ToolResult> ListCheckpointsAsync(CancellationToken ct)
    {
        if (!System.IO.Directory.Exists(_checkpointDir))
            return Task.FromResult(ToolResult.Ok("No checkpoints found."));

        var dirs = System.IO.Directory.GetDirectories(_checkpointDir)
            .Select(d => new DirectoryInfo(d))
            .OrderByDescending(d => d.CreationTimeUtc)
            .Select(d => $"{d.Name}  (created {d.CreationTimeUtc:yyyy-MM-dd HH:mm:ss} UTC)")
            .ToArray();

        if (dirs.Length == 0)
            return Task.FromResult(ToolResult.Ok("No checkpoints found."));

        return Task.FromResult(ToolResult.Ok(string.Join("\n", dirs)));
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in System.IO.Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var subDir in System.IO.Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, destSubDir);
        }
    }
}

public sealed class CheckpointParameters
{
    public required string Action { get; init; }
    public string? Name { get; init; }
    public string? Directory { get; init; }
}
