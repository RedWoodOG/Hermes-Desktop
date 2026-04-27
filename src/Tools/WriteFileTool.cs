namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using System.Text.Json;

/// <summary>
/// File writing tool with structured patch and git diff output.
/// </summary>
public sealed class WriteFileTool : ITool
{
    public string Name => "write_file";
    public string Description => "Create new files or overwrite existing ones with structured patch output";
    public Type ParametersType => typeof(WriteFileParameters);
    
    public Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (WriteFileParameters)parameters;
        return WriteFileAsync(p.FilePath, p.Content, ct);
    }
    
    private async Task<ToolResult> WriteFileAsync(string filePath, string content, CancellationToken ct)
    {
        try
        {
            var exists = File.Exists(filePath);

            // Check for stale file content before writing
            var staleWarning = FileReadTracker.CheckStaleness(filePath);

            // Read old content for diff if file exists
            string? oldContent = null;
            if (exists)
            {
                try { oldContent = await File.ReadAllTextAsync(filePath, ct); }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"WriteFileTool old-content read failed for {filePath}: {ex}");
                }
            }

            // Create directory if needed
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Write file
            await File.WriteAllTextAsync(filePath, content, ct);

            // Update tracker so consecutive writes don't trigger false warnings
            FileReadTracker.UpdateAfterWrite(filePath);

            // Generate inline diff if we had old content
            var diff = oldContent is not null
                ? DiffHelper.UnifiedDiff(oldContent, content, Path.GetFileName(filePath))
                : null;

            // Generate structured output
            var result = new
            {
                status = "success",
                action = exists ? "overwrote" : "created",
                path = filePath,
                lines = content.Split('\n').Length,
                _warning = staleWarning,
                diff = diff,
                structured_patch = GeneratePatch(filePath, content)
            };

            return ToolResult.Ok(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to write file: {ex.Message}", ex);
        }
    }
    
    private string GeneratePatch(string filePath, string content)
    {
        // Simple patch format - in production would use libgit2sharp for real git diff
        return $"""
            --- a/{filePath}
            +++ b/{filePath}
            @@ -0,0 +1,{content.Split('\n').Length} @@
            """ + "\n" + string.Join("\n", content.Split('\n').Select(line => $"+{line}"));
    }
}

public sealed class WriteFileParameters
{
    public required string FilePath { get; init; }
    public required string Content { get; init; }
}
