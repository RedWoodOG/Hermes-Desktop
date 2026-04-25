namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using System.Diagnostics;

/// <summary>
/// Fast content search using ripgrep (rg).
/// </summary>
public sealed class GrepTool : ITool
{
    public string Name => "grep";
    public string Description => "Fast content search using ripgrep with regex support";
    public Type ParametersType => typeof(GrepParameters);
    
    public Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (GrepParameters)parameters;
        return SearchAsync(p.Pattern, p.Path, p.Include, p.OutputMode, ct);
    }
    
    private async Task<ToolResult> SearchAsync(string pattern, string? path, string? include, string outputMode, CancellationToken ct)
    {
        try
        {
            // Check if ripgrep is available
            var rgPath = FindRipgrep();
            if (rgPath == null)
            {
                return ToolResult.Fail("ripgrep (rg) not found. Install from https://github.com/BurntSushi/ripgrep");
            }
            
            var args = new List<string> { "--json" };
            
            if (!string.IsNullOrEmpty(path))
                args.Add(path);
            
            if (!string.IsNullOrEmpty(include))
                args.AddRange(new[] { "--glob", include });
            
            args.AddRange(new[] { "--", pattern });
            
            var psi = new ProcessStartInfo
            {
                FileName = rgPath,
                Arguments = string.Join(" ", args),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            
            using var process = new Process { StartInfo = psi };
            process.Start();
            
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(ct);
            
            if (outputMode == "files_with_matches")
            {
                // Extract unique file paths from JSON output
                var files = ParseRipgrepFiles(output);
                return ToolResult.Ok(string.Join("\n", files));
            }
            else if (outputMode == "count")
            {
                var counts = ParseRipgrepCounts(output);
                return ToolResult.Ok(string.Join("\n", counts.Select(kvp => $"{kvp.Value} {kvp.Key}")));
            }
            else // content
            {
                return ToolResult.Ok(output);
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Search failed: {ex.Message}", ex);
        }
    }
    
    private string? FindRipgrep()
    {
        // Check PATH
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();
        foreach (var path in paths)
        {
            var rgPath = Path.Combine(path, "rg.exe");
            if (File.Exists(rgPath))
                return rgPath;
        }
        
        // Common installation locations
        var commonPaths = new[]
        {
            @"C:\Program Files\ripgrep\rg.exe",
            @"C:\Program Files (x86)\ripgrep\rg.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop\\apps\\ripgrep\\current\\rg.exe")
        };
        
        foreach (var p in commonPaths)
        {
            if (File.Exists(p))
                return p;
        }
        
        return null;
    }
    
    private List<string> ParseRipgrepFiles(string jsonOutput)
    {
        // Parse ripgrep JSON output to extract file paths
        var files = new HashSet<string>();
        // Simple parsing - in production use System.Text.Json
        var lines = jsonOutput.Split('\n');
        foreach (var line in lines)
        {
            if (line.Contains("\"path\":"))
            {
                var start = line.IndexOf("\"path\":\"") + 8;
                var end = line.IndexOf("\"", start);
                if (start > 7 && end > start)
                    files.Add(line.Substring(start, end - start));
            }
        }
        return files.ToList();
    }
    
    private Dictionary<string, int> ParseRipgrepCounts(string jsonOutput)
    {
        var counts = new Dictionary<string, int>();
        // Simple parsing - in production use System.Text.Json
        return counts;
    }
}

public sealed class GrepParameters
{
    public required string Pattern { get; init; }
    public string? Path { get; init; }
    public string? Include { get; init; }
    public string OutputMode { get; init; } = "files_with_matches"; // files_with_matches, content, count
}
