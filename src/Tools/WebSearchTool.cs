namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Web search tool supporting multiple search providers.
/// </summary>
public sealed class WebSearchTool : ITool
{
    private readonly HttpClient _httpClient;
    private readonly WebSearchConfig _config;
    
    public string Name => "websearch";
    public string Description => "Search the web using configured search provider";
    public Type ParametersType => typeof(WebSearchParameters);
    
    public WebSearchTool(WebSearchConfig config, HttpClient? httpClient = null)
    {
        _config = config;
        _httpClient = httpClient ?? new HttpClient();
    }
    
    public async Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (WebSearchParameters)parameters;
        
        try
        {
            var results = _config.Provider.ToLowerInvariant() switch
            {
                "duckduckgo" or "ddg" => await SearchDuckDuckGoAsync(p.Query, p.MaxResults, ct),
                "google" => await SearchGoogleAsync(p.Query, p.MaxResults, ct),
                "bing" => await SearchBingAsync(p.Query, p.MaxResults, ct),
                _ => throw new NotSupportedException($"Search provider '{_config.Provider}' not supported")
            };
            
            var output = FormatResults(results);
            return ToolResult.Ok(output);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Search failed: {ex.Message}", ex);
        }
    }
    
    private async Task<List<SearchResult>> SearchDuckDuckGoAsync(string query, int maxResults, CancellationToken ct)
    {
        // DuckDuckGo Instant Answer API (limited but free)
        var url = $"https://api.duckduckgo.com/?q={Uri.EscapeDataString(query)}&format=json&no_html=1";
        
        var response = await _httpClient.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;
        
        var results = new List<SearchResult>();
        
        // Related topics
        if (root.TryGetProperty("RelatedTopics", out var topics) && topics.ValueKind == JsonValueKind.Array)
        {
            foreach (var topic in topics.EnumerateArray().Take(maxResults))
            {
                if (topic.TryGetProperty("Text", out var textProp) &&
                    topic.TryGetProperty("FirstURL", out var urlProp))
                {
                    results.Add(new SearchResult(
                        textProp.GetString() ?? "",
                        urlProp.GetString() ?? "",
                        "DuckDuckGo"
                    ));
                }
            }
        }
        
        // Abstract
        if (root.TryGetProperty("Abstract", out var abstractProp) && abstractProp.ValueKind == JsonValueKind.String)
        {
            var abstractText = abstractProp.GetString();
            if (!string.IsNullOrEmpty(abstractText))
            {
                var abstractUrl = root.TryGetProperty("AbstractURL", out var urlProp) 
                    ? urlProp.GetString() ?? "" 
                    : "";
                    
                results.Insert(0, new SearchResult(abstractText, abstractUrl, "DuckDuckGo Instant Answer"));
            }
        }
        
        return results;
    }
    
    private async Task<List<SearchResult>> SearchGoogleAsync(string query, int maxResults, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_config.GoogleApiKey) || string.IsNullOrEmpty(_config.GoogleSearchEngineId))
        {
            throw new InvalidOperationException("Google search requires API key and Search Engine ID");
        }
        
        var url = $"https://www.googleapis.com/customsearch/v1?key={_config.GoogleApiKey}&cx={_config.GoogleSearchEngineId}&q={Uri.EscapeDataString(query)}&num={maxResults}";
        
        var response = await _httpClient.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;
        
        var results = new List<SearchResult>();
        
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
                var link = item.TryGetProperty("link", out var linkProp) ? linkProp.GetString() ?? "" : "";
                var snippet = item.TryGetProperty("snippet", out var snippetProp) ? snippetProp.GetString() ?? "" : "";
                
                results.Add(new SearchResult($"{title}\n{snippet}", link, "Google"));
            }
        }
        
        return results;
    }
    
    private async Task<List<SearchResult>> SearchBingAsync(string query, int maxResults, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_config.BingApiKey))
        {
            throw new InvalidOperationException("Bing search requires API key");
        }
        
        var request = new HttpRequestMessage(HttpMethod.Get, 
            $"https://api.bing.microsoft.com/v7.0/search?q={Uri.EscapeDataString(query)}&count={maxResults}");
        request.Headers.Add("Ocp-Apim-Subscription-Key", _config.BingApiKey);
        
        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        
        var responseStr = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(responseStr);
        var root = doc.RootElement;
        
        var results = new List<SearchResult>();
        
        if (root.TryGetProperty("webPages", out var webPages) &&
            webPages.TryGetProperty("value", out var items) &&
            items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                var url = item.TryGetProperty("url", out var urlProp) ? urlProp.GetString() ?? "" : "";
                var snippet = item.TryGetProperty("snippet", out var snippetProp) ? snippetProp.GetString() ?? "" : "";
                
                results.Add(new SearchResult($"{name}\n{snippet}", url, "Bing"));
            }
        }
        
        return results;
    }
    
    private static string FormatResults(List<SearchResult> results)
    {
        if (results.Count == 0)
        {
            return "No results found.";
        }
        
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            sb.AppendLine($"[{i + 1}] {r.Title}");
            sb.AppendLine($"    URL: {r.Url}");
            sb.AppendLine($"    Source: {r.Source}");
            sb.AppendLine();
        }
        
        return sb.ToString();
    }
}

public sealed record SearchResult(string Title, string Url, string Source);

public sealed class WebSearchConfig
{
    public string Provider { get; init; } = "duckduckgo";
    public string? GoogleApiKey { get; init; }
    public string? GoogleSearchEngineId { get; init; }
    public string? BingApiKey { get; init; }

    /// <summary>The providers WebSearchTool actually knows how to call.</summary>
    public static readonly IReadOnlyCollection<string> SupportedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "duckduckgo", "google", "bing"
    };

    /// <summary>
    /// Normalize a persisted provider string to one of <see cref="SupportedProviders"/>.
    /// Null, whitespace, or unknown values fall back to <c>duckduckgo</c> so a typo
    /// in config.yaml can't turn every web-search call into NotSupportedException.
    /// </summary>
    public static string NormalizeProvider(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "duckduckgo";
        var trimmed = raw.Trim().ToLowerInvariant();
        // "ddg" is the other alias ExecuteAsync accepts.
        if (trimmed == "ddg") return "duckduckgo";
        return SupportedProviders.Contains(trimmed) ? trimmed : "duckduckgo";
    }
}

public sealed class WebSearchParameters
{
    public required string Query { get; init; }
    public int MaxResults { get; init; } = 5;
}
