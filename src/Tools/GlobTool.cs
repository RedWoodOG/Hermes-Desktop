namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using System.Text.RegularExpressions;

/// <summary>
/// Fast file pattern matching using glob patterns.
/// </summary>
public sealed class GlobTool : ITool
{
    public string Name => "glob";
    public string Description => "Fast file pattern matching for finding files by name patterns (e.g., **/*.cs)";
    public Type ParametersType => typeof(GlobParameters);
    
    public Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (GlobParameters)parameters;
        return FindFilesAsync(p.Pattern, p.Path, ct);
    }
    
    private Task<ToolResult> FindFilesAsync(string pattern, string? path, CancellationToken ct)
    {
        try
        {
            var searchDir = path ?? Directory.GetCurrentDirectory();
            
            if (!Directory.Exists(searchDir))
            {
                return Task.FromResult(ToolResult.Fail($"Directory not found: {searchDir}"));
            }
            
            // Convert glob pattern to search pattern
            var searchPattern = "**/*"; // Default recursive
            var option = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true
            };
            
            var files = Directory.EnumerateFiles(searchDir, searchPattern, option)
                .Where(f => MatchesGlob(f, pattern, searchDir))
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .Take(100)
                .ToList();
            
            var output = files.Count == 0 
                ? "No files found matching pattern."
                : string.Join("\n", files);
            
            return Task.FromResult(ToolResult.Ok(output));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Failed to search files: {ex.Message}", ex));
        }
    }
    
    private bool MatchesGlob(string filePath, string pattern, string baseDir)
    {
        var relativePath = Path.GetRelativePath(baseDir, filePath);
        var globPattern = pattern.Replace("**/", "**\\").Replace("/", "\\");
        
        // Simple glob matching - in production use Microsoft.Extensions.FileSystemGlobbing
        return System.Text.RegularExpressions.Regex.IsMatch(
            relativePath,
            "^" + Regex.Escape(globPattern).Replace("\\*\\*", ".*").Replace("\\*", "[^\\\\]*") + "$",
            RegexOptions.IgnoreCase);
    }
}

public sealed class GlobParameters
{
    public required string Pattern { get; init; }
    public string? Path { get; init; }
}
