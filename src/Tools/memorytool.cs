namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using System.Text.Json;

/// <summary>
/// Manage persistent memories — save, list, and delete .md memory files.
/// </summary>
public sealed class MemoryTool : ITool
{
    private readonly string _memoryDir;

    public string Name => "memory";
    public string Description => "Manage persistent memories. Save, list, or delete memory files.";
    public Type ParametersType => typeof(MemoryToolParameters);

    public MemoryTool(string memoryDir)
    {
        _memoryDir = memoryDir;
        Directory.CreateDirectory(_memoryDir);
    }

    public Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (MemoryToolParameters)parameters;

        return p.Action?.ToLowerInvariant() switch
        {
            "save" => SaveMemoryAsync(p.Content, ct),
            "list" => ListMemoriesAsync(ct),
            "delete" => DeleteMemoryAsync(p.Filename, ct),
            _ => Task.FromResult(ToolResult.Fail($"Unknown action: {p.Action}. Use save, list, or delete."))
        };
    }

    private async Task<ToolResult> SaveMemoryAsync(string? content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content))
            return ToolResult.Fail("Content is required for save_memory.");

        var filename = $"memory_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}.md";
        var filePath = Path.Combine(_memoryDir, filename);

        await File.WriteAllTextAsync(filePath, content, ct);
        return ToolResult.Ok($"Memory saved: {filename}");
    }

    private Task<ToolResult> ListMemoriesAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_memoryDir))
            return Task.FromResult(ToolResult.Ok("No memories found."));

        var files = Directory.GetFiles(_memoryDir, "*.md")
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

        var filePath = Path.Combine(_memoryDir, filename);

        if (!File.Exists(filePath))
            return Task.FromResult(ToolResult.Fail($"Memory file not found: {filename}"));

        File.Delete(filePath);
        return Task.FromResult(ToolResult.Ok($"Memory deleted: {filename}"));
    }
}

public sealed class MemoryToolParameters
{
    public required string Action { get; init; }
    public string? Content { get; init; }
    public string? Filename { get; init; }
}
