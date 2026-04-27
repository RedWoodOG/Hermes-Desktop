namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using Hermes.Agent.Security;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Fetches content from URLs with SSRF protection.
/// </summary>
public sealed class WebFetchTool : ITool
{
    private readonly HttpClient _httpClient;
    private readonly SsrfGuard _ssrfGuard;
    
    public string Name => "webfetch";
    public string Description => "Fetch content from a URL with SSRF protection and content extraction";
    public Type ParametersType => typeof(WebFetchParameters);
    
    public WebFetchTool(HttpClient? httpClient = null, SsrfGuard? ssrfGuard = null)
    {
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false // Prevent SSRF via redirect chains to private IPs
        });
        _ssrfGuard = ssrfGuard ?? new SsrfGuard();
    }
    
    public async Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (WebFetchParameters)parameters;
        
        try
        {
            // Check for embedded credentials in URL
            if (SecretScanner.UrlContainsCredentials(p.Url))
            {
                return ToolResult.Fail("URL contains embedded credentials. Remove credentials from the URL before fetching.");
            }

            // Validate URL for SSRF
            var validationResult = await _ssrfGuard.ValidateAsync(p.Url, ct);
            if (!validationResult.IsValid)
            {
                return ToolResult.Fail($"SSRF protection: {validationResult.Reason}");
            }
            
            // Set timeout
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(p.TimeoutMs));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            
            // Add headers
            var request = new HttpRequestMessage(HttpMethod.Get, p.Url);
            if (!string.IsNullOrEmpty(p.UserAgent))
            {
                request.Headers.Add("User-Agent", p.UserAgent);
            }
            else
            {
                request.Headers.Add("User-Agent", "Hermes-Agent/1.0");
            }
            
            // Follow redirects manually with SSRF validation on each hop
            var response = await _httpClient.SendAsync(request, linkedCts.Token);
            var maxRedirects = 5;
            while (maxRedirects-- > 0 &&
                   (int)response.StatusCode >= 300 && (int)response.StatusCode < 400 &&
                   response.Headers.Location is { } redirectUri)
            {
                var target = redirectUri.IsAbsoluteUri
                    ? redirectUri.ToString()
                    : new Uri(new Uri(p.Url), redirectUri).ToString();

                var redirectValidation = await _ssrfGuard.ValidateAsync(target, linkedCts.Token);
                if (!redirectValidation.IsValid)
                    return ToolResult.Fail($"SSRF protection on redirect: {redirectValidation.Reason}");

                response.Dispose();
                response = await _httpClient.GetAsync(target, linkedCts.Token);
            }
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(linkedCts.Token);
            
            // Extract content based on type
            if (p.ExtractText && IsHtmlContent(response))
            {
                content = ExtractTextFromHtml(content);
            }
            
            // Truncate if needed
            if (content.Length > p.MaxLength)
            {
                content = content.Substring(0, p.MaxLength) + "\n... (truncated)";
            }
            
            return ToolResult.Ok(content);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail($"Request timed out after {p.TimeoutMs}ms");
        }
        catch (HttpRequestException ex)
        {
            return ToolResult.Fail($"HTTP error: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to fetch URL: {ex.Message}", ex);
        }
    }
    
    private static bool IsHtmlContent(HttpResponseMessage response)
    {
        var contentType = response.Content.Headers.ContentType?.MediaType;
        return contentType?.Contains("html") == true;
    }
    
    private static string ExtractTextFromHtml(string html)
    {
        // Remove scripts and styles
        html = Regex.Replace(html, @"<script[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        
        // Remove HTML tags
        html = Regex.Replace(html, @"<[^>]+>", " ");
        
        // Decode HTML entities
        html = System.Web.HttpUtility.HtmlDecode(html);
        
        // Normalize whitespace
        html = Regex.Replace(html, @"\s+", " ");
        
        return html.Trim();
    }
}

/// <summary>
/// SSRF (Server-Side Request Forgery) protection.
/// </summary>
public sealed class SsrfGuard
{
    private static readonly string[] PrivateIpRanges =
    {
        "10.",      // 10.0.0.0/8
        "172.16.", "172.17.", "172.18.", "172.19.", "172.20.", "172.21.", "172.22.", "172.23.",
        "172.24.", "172.25.", "172.26.", "172.27.", "172.28.", "172.29.", "172.30.", "172.31.", // 172.16.0.0/12
        "192.168.", // 192.168.0.0/16
        "127.",     // 127.0.0.0/8 (localhost)
        "169.254.", // 169.254.0.0/16 (link-local)
        "::1",      // IPv6 localhost
        "fc00:", "fd00:", // IPv6 ULA
        "fe80:",    // IPv6 link-local
    };
    
    private static readonly string[] BlockedHosts =
    {
        "localhost",
        "localhost.localdomain",
        "ip6-localhost",
        "ip6-loopback",
        "metadata.google.internal",  // GCP metadata
        "169.254.169.254",           // Cloud metadata
    };
    
    public async Task<SsrfValidationResult> ValidateAsync(string url, CancellationToken ct)
    {
        Uri uri;
        try
        {
            uri = new Uri(url);
        }
        catch (UriFormatException)
        {
            return SsrfValidationResult.Invalid("Invalid URL format");
        }
        
        // Check scheme
        if (uri.Scheme != "http" && uri.Scheme != "https")
        {
            return SsrfValidationResult.Invalid("Only HTTP and HTTPS URLs are allowed");
        }
        
        var host = uri.Host.ToLowerInvariant();
        
        // Check blocked hosts
        if (BlockedHosts.Contains(host))
        {
            return SsrfValidationResult.Invalid("Access to this host is blocked");
        }
        
        // Resolve DNS and check IP
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(host, ct);
            
            foreach (var addr in addresses)
            {
                var ipStr = addr.ToString();
                
                foreach (var range in PrivateIpRanges)
                {
                    if (ipStr.StartsWith(range))
                    {
                        return SsrfValidationResult.Invalid($"Access to private IP range blocked: {ipStr}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return SsrfValidationResult.Invalid($"DNS resolution failed: {ex.Message}");
        }
        
        return SsrfValidationResult.Valid();
    }
}

public sealed record SsrfValidationResult(bool IsValid, string? Reason = null)
{
    public static SsrfValidationResult Valid() => new(true);
    public static SsrfValidationResult Invalid(string reason) => new(false, reason);
}

public sealed class WebFetchParameters
{
    public required string Url { get; init; }
    public int TimeoutMs { get; init; } = 30000;
    public int MaxLength { get; init; } = 50000;
    public bool ExtractText { get; init; } = true;
    public string? UserAgent { get; init; }
}
