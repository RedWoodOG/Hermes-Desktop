namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using System.Text.Json;

/// <summary>
/// Search past conversation sessions by keyword.
/// Scans transcript .jsonl files for matching content.
/// </summary>
public sealed class SessionSearchTool : ITool
{
    private readonly string _transcriptDir;

    public string Name => "session_search";
    public string Description => "Search past conversation sessions by keyword. Returns matching session IDs and message snippets.";
    public Type ParametersType => typeof(SessionSearchParameters);

    public SessionSearchTool(string transcriptDir)
    {
        _transcriptDir = transcriptDir;
    }

    public async Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (SessionSearchParameters)parameters;

        if (string.IsNullOrWhiteSpace(p.Query))
            return ToolResult.Fail("Query is required.");

        var maxResults = p.MaxResults > 0 ? p.MaxResults : 5;

        try
        {
            if (!Directory.Exists(_transcriptDir))
                return ToolResult.Ok("No transcripts found.");

            var jsonlFiles = Directory.GetFiles(_transcriptDir, "*.jsonl", SearchOption.AllDirectories);
            var results = new List<SearchHit>();

            foreach (var file in jsonlFiles)
            {
                if (ct.IsCancellationRequested) break;

                var sessionId = Path.GetFileNameWithoutExtension(file);
                var lines = await File.ReadAllLinesAsync(file, ct);

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    if (line.Contains(p.Query, StringComparison.OrdinalIgnoreCase))
                    {
                        // Extract a snippet around the match
                        var snippet = ExtractSnippet(line, p.Query);
                        results.Add(new SearchHit(sessionId, snippet));

                        if (results.Count >= maxResults)
                            break;
                    }
                }

                if (results.Count >= maxResults)
                    break;
            }

            if (results.Count == 0)
                return ToolResult.Ok($"No sessions found matching '{p.Query}'.");

            var output = results.Select(r => $"[{r.SessionId}] {r.Snippet}");
            return ToolResult.Ok(string.Join("\n\n", output));
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Search failed: {ex.Message}", ex);
        }
    }

    private static string ExtractSnippet(string line, string query)
    {
        // Try to parse the JSONL line to get the content field
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("Content", out var contentEl) ||
                doc.RootElement.TryGetProperty("content", out contentEl))
            {
                var content = contentEl.GetString() ?? line;
                return TruncateAround(content, query, 150);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SessionSearchTool failed to parse structured line: {ex}");
        }

        return TruncateAround(line, query, 150);
    }

    private static string TruncateAround(string text, string query, int maxLen)
    {
        var idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = 0;

        var start = Math.Max(0, idx - (maxLen / 2));
        var end = Math.Min(text.Length, start + maxLen);
        start = Math.Max(0, end - maxLen);

        var snippet = text[start..end].Trim();
        if (start > 0) snippet = "..." + snippet;
        if (end < text.Length) snippet += "...";
        return snippet;
    }

    private sealed record SearchHit(string SessionId, string Snippet);
}

public sealed class SessionSearchParameters
{
    public required string Query { get; init; }
    public int MaxResults { get; init; } = 5;
}
