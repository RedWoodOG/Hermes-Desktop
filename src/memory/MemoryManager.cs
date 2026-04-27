namespace Hermes.Agent.Memory;

using Hermes.Agent.LLM;
using Hermes.Agent.Core;
using Microsoft.Extensions.Logging;

/// <summary>
/// Persistent memory system with file-based storage, relevance scanning, and freshness warnings.
/// Location: ~/.hermes-cs/projects/<git-root>/memory/
/// </summary>
public sealed class MemoryManager
{
    private readonly string _memoryDir;
    private readonly IChatClient _chatClient;
    private readonly ILogger<MemoryManager> _logger;

    public string MemoryDir => _memoryDir;

    public MemoryManager(string memoryDir, IChatClient chatClient, ILogger<MemoryManager> logger)
    {
        _memoryDir = memoryDir;
        _chatClient = chatClient;
        _logger = logger;
        
        Directory.CreateDirectory(memoryDir);
    }
    
    /// <summary>
    /// Load relevant memories for current query.
    /// Scans files, selects top 5 by relevance, adds freshness warnings.
    /// </summary>
    public async Task<List<MemoryContext>> LoadRelevantMemoriesAsync(
        string query, 
        List<string> recentTools,
        CancellationToken ct)
    {
        _logger.LogDebug("Loading relevant memories for query: {Query}", query.Take(50));
        
        // 1. Scan all memory files (frontmatter only)
        var headers = await ScanMemoryFilesAsync(_memoryDir, ct);
        _logger.LogDebug("Found {Count} memory files", headers.Count);
        
        // 2. Filter out already-surfaced memories (track in session state)
        var freshHeaders = headers.Where(h => !h.AlreadySurfaced).ToList();
        
        if (freshHeaders.Count == 0)
        {
            _logger.LogDebug("No fresh memories to load");
            return new List<MemoryContext>();
        }
        
        // 3. Use LLM to select most relevant (up to 5)
        var relevant = await SelectRelevantMemoriesAsync(query, freshHeaders, recentTools, ct);
        _logger.LogDebug("Selected {Count} relevant memories", relevant.Count);
        
        // 4. Load full content + add freshness warnings
        var memories = new List<MemoryContext>();
        foreach (var mem in relevant)
        {
            var content = await File.ReadAllTextAsync(mem.Path, ct);
            var warning = GetFreshnessWarning(mem.Mtime);
            
            memories.Add(new MemoryContext
            {
                Path = mem.Path,
                Filename = mem.Filename,
                Content = content,
                FreshnessWarning = warning,
                Type = mem.Type
            });
        }
        
        return memories;
    }
    
    /// <summary>
    /// Scan memory directory for all .md files with frontmatter.
    /// Returns headers sorted by modification time (newest first).
    /// </summary>
    private async Task<List<MemoryHeader>> ScanMemoryFilesAsync(string dir, CancellationToken ct)
    {
        var headers = new List<MemoryHeader>();
        
        if (!Directory.Exists(dir))
            return headers;
        
        // Find all .md files (exclude MEMORY.md entrypoint)
        var files = Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("MEMORY.md", StringComparison.OrdinalIgnoreCase))
            .Take(200); // Cap at 200 files
        
        foreach (var file in files)
        {
            try
            {
                var header = await ParseFrontmatterAsync(file, ct);
                if (header != null)
                    headers.Add(header);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse frontmatter for {File}", file);
            }
        }
        
        // Sort by modification time (newest first)
        return headers.OrderByDescending(h => h.Mtime).ToList();
    }
    
    /// <summary>
    /// Parse YAML frontmatter from first 30 lines of file.
    /// </summary>
    private async Task<MemoryHeader?> ParseFrontmatterAsync(string path, CancellationToken ct)
    {
        var lines = await File.ReadAllLinesAsync(path, ct);
        
        // Parse YAML frontmatter (first 30 lines max)
        // Format:
        // ---
        // name: My Memory
        // description: One-line description
        // type: user
        // ---
        if (lines.Length < 2 || lines[0].Trim() != "---")
            return null;
        
        var endIndex = Array.FindIndex(lines, 1, l => l.Trim() == "---");
        if (endIndex == -1)
            return null;
        
        var yamlLines = lines.Skip(1).Take(endIndex - 1).ToList();
        var yaml = string.Join("\n", yamlLines);
        
        try
        {
            var frontmatter = Yaml.Deserialize<MemoryFrontmatter>(yaml);
            
            return new MemoryHeader
            {
                Path = path,
                Filename = Path.GetFileName(path),
                Mtime = File.GetLastWriteTimeUtc(path),
                Name = frontmatter.Name,
                Description = frontmatter.Description,
                Type = frontmatter.Type
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse YAML for {File}", path);
            return null;
        }
    }
    
    /// <summary>
    /// Use LLM to select most relevant memories for query.
    /// Returns up to 5 memories.
    /// </summary>
    private async Task<List<MemoryHeader>> SelectRelevantMemoriesAsync(
        string query, 
        List<MemoryHeader> candidates,
        List<string> recentTools,
        CancellationToken ct)
    {
        if (candidates.Count <= 5)
            return candidates; // All are relevant
        
        // Build manifest for LLM
        var manifest = string.Join("\n", candidates.Select((h, i) => 
            $"{i + 1}. [{h.Type}] {h.Filename}: {h.Description}"));
        
        var prompt = $@"
You are selecting relevant memories for this query.

Query: {query}
Recent tools used: {string.Join(", ", recentTools)}

Available memories:
{manifest}

Select up to 5 most relevant memories. Return ONLY the numbers (1-5), comma-separated.
Example: 1, 3, 5

Selection:";
        
        try
        {
            var response = await _chatClient.CompleteAsync(
                new[] { new Message { Role = "user", Content = prompt } }, ct);
            
            // Parse response (expect numbers like "1, 3, 5")
            var numbers = response.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), out var n) ? n : 0)
                .Where(n => n > 0 && n <= candidates.Count)
                .Distinct()
                .Take(5)
                .ToList();
            
            if (numbers.Count == 0)
                return candidates.Take(5).ToList(); // Fallback to first 5
            
            return numbers.Select(n => candidates[n - 1]).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to select relevant memories");
            return candidates.Take(5).ToList(); // Fallback
        }
    }
    
    /// <summary>
    /// Get freshness warning based on memory age.
    /// </summary>
    private string? GetFreshnessWarning(DateTime mtime)
    {
        var days = (DateTime.UtcNow - mtime).TotalDays;
        
        if (days < 1)
            return null; // Too fresh, no warning
        
        var daysText = days switch
        {
            < 2 => "1 day",
            < 7 => $"{(int)days} days",
            < 30 => $"{(int)(days / 7)} weeks",
            _ => $"{(int)(days / 30)} months"
        };
        
        return $"<system-reminder>This memory is {daysText} old. " +
               $"Memories are point-in-time observations, not live state. " +
               $"Verify against current code before asserting as fact.</system-reminder>";
    }
    
    /// <summary>
    /// Save a memory file.
    /// </summary>
    public async Task SaveMemoryAsync(string filename, string content, string type, CancellationToken ct)
    {
        var path = Path.Combine(_memoryDir, filename);
        
        // Ensure frontmatter exists
        if (!content.StartsWith("---"))
        {
            var frontmatter = $@"---
name: {Path.GetFileNameWithoutExtension(filename)}
description: Auto-generated memory
type: {type}
---
";
            content = frontmatter + content;
        }
        
        await File.WriteAllTextAsync(path, content, ct);
        _logger.LogInformation("Saved memory: {Filename}", filename);
    }
    
    /// <summary>
    /// Delete a memory file.
    /// </summary>
    public async Task DeleteMemoryAsync(string filename, CancellationToken ct)
    {
        var path = Path.Combine(_memoryDir, filename);
        
        if (File.Exists(path))
        {
            File.Delete(path);
            _logger.LogInformation("Deleted memory: {Filename}", filename);
        }
    }
    
    /// <summary>
    /// Load all memories (for admin/management).
    /// </summary>
    public async Task<List<MemoryContext>> LoadAllMemoriesAsync(CancellationToken ct)
    {
        var headers = await ScanMemoryFilesAsync(_memoryDir, ct);
        var memories = new List<MemoryContext>();
        
        foreach (var header in headers)
        {
            var content = await File.ReadAllTextAsync(header.Path, ct);
            memories.Add(new MemoryContext
            {
                Path = header.Path,
                Filename = header.Filename,
                Content = content,
                Type = header.Type
            });
        }
        
        return memories;
    }
}

// =============================================
// Memory Types
// =============================================

public sealed class MemoryFrontmatter
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "user"; // user, feedback, project, reference
}

public sealed class MemoryHeader
{
    public required string Path { get; init; }
    public required string Filename { get; init; }
    public DateTime Mtime { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? Type { get; init; }
    public bool AlreadySurfaced { get; set; }
}

public sealed class MemoryContext
{
    public required string Path { get; init; }
    public required string Filename { get; init; }
    public required string Content { get; init; }
    public string? FreshnessWarning { get; init; }
    public string? Type { get; init; }
}

// =============================================
// YAML Helper (simple parser)
// =============================================

public static class Yaml
{
    public static T Deserialize<T>(string yaml) where T : new()
    {
        var obj = new T();
        var properties = typeof(T).GetProperties();
        
        foreach (var line in yaml.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                continue;
            
            var colonIndex = trimmed.IndexOf(':');
            if (colonIndex == -1)
                continue;
            
            var key = trimmed.Substring(0, colonIndex).Trim();
            var value = trimmed.Substring(colonIndex + 1).Trim().Trim('"', '\'');
            
            foreach (var prop in properties)
            {
                if (prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    var converted = Convert.ChangeType(value, prop.PropertyType);
                    prop.SetValue(obj, converted);
                    break;
                }
            }
        }
        
        return obj;
    }
}
