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
        return ReadFileAsync(p.FilePath, p.Offset, p.Limit, ct);
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
