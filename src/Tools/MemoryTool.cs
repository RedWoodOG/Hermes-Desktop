namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using Hermes.Agent.Memory;
using System.Text.Json;

/// <summary>
/// Manage persistent memories — save, list, and delete .md memory files.
/// Delegates save to MemoryManager so files always carry the YAML frontmatter
/// that auto-recall scans for.
/// </summary>
public sealed class MemoryTool : ITool
{
    private readonly MemoryManager _memoryManager;

    public string Name => "memory";
    public string Description => "Manage persistent memories. Save, list, or delete memory files.";
    public Type ParametersType => typeof(MemoryToolParameters);

    public MemoryTool(MemoryManager memoryManager)
    {
        _memoryManager = memoryManager;
    }

    private string MemoryDir => _memoryManager.MemoryDir;

    public Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (MemoryToolParameters)parameters;

        return p.Action?.ToLowerInvariant() switch
        {
            "save" => SaveMemoryAsync(p.Content, p.Type, ct),
            "list" => ListMemoriesAsync(ct),
            "delete" => DeleteMemoryAsync(p.Filename, ct),
            _ => Task.FromResult(ToolResult.Fail($"Unknown action: {p.Action}. Use save, list, or delete."))
        };
    }

    private async Task<ToolResult> SaveMemoryAsync(string? content, string? type, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content))
            return ToolResult.Fail("Content is required for save_memory.");

        var filename = $"memory_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}.md";
        await _memoryManager.SaveMemoryAsync(filename, content, type ?? "user", ct);
        return ToolResult.Ok($"Memory saved: {filename}");
    }

    private Task<ToolResult> ListMemoriesAsync(CancellationToken ct)
    {
        if (!Directory.Exists(MemoryDir))
            return Task.FromResult(ToolResult.Ok("No memories found."));

        var files = Directory.GetFiles(MemoryDir, "*.md")
            .Select(Path.GetFileName)
            .OrderByDescending(f => f)
            .ToArray();

        if (files.Length == 0)
            return Task.FromResult(ToolResult.Ok("No memories found."));

        return Task.FromResult(ToolResult.Ok(string.Join("\n", files!)));
    }

    private Task<ToolResult> DeleteMemoryAsync(string? filename, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return Task.FromResult(ToolResult.Fail("Filename is required for delete_memory."));

        // Reject path traversal / absolute paths / separators — filename must be a bare name.
        if (filename.Contains("..") ||
            filename.Contains('/') ||
            filename.Contains('\\') ||
            Path.IsPathRooted(filename) ||
            !string.Equals(Path.GetFileName(filename), filename, StringComparison.Ordinal))
        {
            return Task.FromResult(ToolResult.Fail($"Invalid filename: {filename}"));
        }

        var filePath = Path.Combine(MemoryDir, filename);

        // Defence in depth: ensure the resolved path stays under MemoryDir.
        var resolvedDir = Path.GetFullPath(MemoryDir);
        var resolvedFile = Path.GetFullPath(filePath);
        if (!resolvedFile.StartsWith(
                resolvedDir.EndsWith(Path.DirectorySeparatorChar) ? resolvedDir : resolvedDir + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            return Task.FromResult(ToolResult.Fail($"Invalid filename: {filename}"));
        }

        if (!File.Exists(resolvedFile))
            return Task.FromResult(ToolResult.Fail($"Memory file not found: {filename}"));

        File.Delete(resolvedFile);
        return Task.FromResult(ToolResult.Ok($"Memory deleted: {filename}"));
    }
}

public sealed class MemoryToolParameters
{
    public required string Action { get; init; }
    public string? Content { get; init; }
    public string? Filename { get; init; }
    public string? Type { get; init; }
}
