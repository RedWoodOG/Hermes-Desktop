namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using Microsoft.Playwright;
using System.Text;
using System.Text.RegularExpressions;

// ══════════════════════════════════════════════
// Browser Tool — Proper Playwright API
// ══════════════════════════════════════════════
//
// Upstream ref: tools/browser_tool.py
// Uses Microsoft.Playwright NuGet for headless Chromium.
// Accessibility tree snapshot for text-based page representation.
// SSRF protection on all navigation.

/// <summary>
/// Browser automation via Playwright accessibility tree.
/// Actions: navigate, click, type, scroll, press, js, snapshot, close.
/// Returns page content as structured text, not screenshots.
/// </summary>
public sealed class BrowserTool : ITool, IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Name => "browser";
    public string Description => "Browse the web. Actions: navigate(url), click(selector), type(selector,text), scroll(direction), press(key), js(expression), snapshot, close.";
    public Type ParametersType => typeof(BrowserParameters);

    // SSRF protection
    private static readonly HashSet<string> BlockedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost", "127.0.0.1", "0.0.0.0", "::1",
        "metadata.google.internal", "169.254.169.254"
    };
    private static readonly Regex PrivateIpPattern = new(
        @"^(10\.|172\.(1[6-9]|2[0-9]|3[01])\.|192\.168\.)", RegexOptions.Compiled);
    private static readonly Regex ApiKeyInUrl = new(
        @"[?&](api[_-]?key|token|secret|password|auth)=", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (BrowserParameters)parameters;

        try
        {
            return p.Action.ToLowerInvariant() switch
            {
                "navigate" => await NavigateAsync(p, ct),
                "click" => await ClickAsync(p),
                "type" or "fill" => await TypeAsync(p),
                "scroll" => await ScrollAsync(p),
                "press" => await PressAsync(p),
                "js" or "evaluate" => await EvaluateAsync(p),
                "snapshot" => await SnapshotAsync(),
                "close" => await CloseAsync(),
                _ => ToolResult.Fail("Unknown action. Use: navigate, click, type, scroll, press, js, snapshot, close")
            };
        }
        catch (PlaywrightException ex)
        {
            return ToolResult.Fail($"Browser error: {ex.Message}");
        }
        catch (Exception ex) when (ex.Message.Contains("Playwright"))
        {
            return ToolResult.Fail(
                "Playwright not installed. Run: pwsh -Command \"dotnet tool install --global Microsoft.Playwright.CLI && playwright install chromium\"\n" +
                $"Error: {ex.Message}");
        }
    }

    private async Task EnsureBrowserAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_page is not null) return;

            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true
            });
            _page = await _browser.NewPageAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<ToolResult> NavigateAsync(BrowserParameters p, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(p.Url))
            return ToolResult.Fail("URL is required for navigate action.");

        // SSRF checks
        if (!Uri.TryCreate(p.Url, UriKind.Absolute, out var uri))
            return ToolResult.Fail("Invalid URL format.");
        if (uri.Scheme != "http" && uri.Scheme != "https")
            return ToolResult.Fail("Only HTTP/HTTPS URLs are allowed.");
        if (BlockedHosts.Contains(uri.Host))
            return ToolResult.Fail("Access to this host is blocked.");
        if (PrivateIpPattern.IsMatch(uri.Host))
            return ToolResult.Fail("Access to private IP ranges is blocked.");
        if (ApiKeyInUrl.IsMatch(p.Url))
            return ToolResult.Fail("URL contains API key — blocked for security.");

        await EnsureBrowserAsync();
        await _page!.GotoAsync(p.Url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 30000
        });

        // Post-navigation SSRF check (redirects)
        var finalUrl = _page.Url;
        if (Uri.TryCreate(finalUrl, UriKind.Absolute, out var finalUri) &&
            (BlockedHosts.Contains(finalUri.Host) || PrivateIpPattern.IsMatch(finalUri.Host)))
        {
            await _page.GotoAsync("about:blank");
            return ToolResult.Fail($"Navigation redirected to blocked host: {finalUri.Host}");
        }

        return await SnapshotAsync();
    }

    private async Task<ToolResult> ClickAsync(BrowserParameters p)
    {
        if (string.IsNullOrWhiteSpace(p.Ref))
            return ToolResult.Fail("Selector/ref is required for click.");
        await EnsureBrowserAsync();
        await _page!.ClickAsync(p.Ref, new PageClickOptions { Timeout = 10000 });
        await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        return await SnapshotAsync();
    }

    private async Task<ToolResult> TypeAsync(BrowserParameters p)
    {
        if (string.IsNullOrWhiteSpace(p.Ref))
            return ToolResult.Fail("Selector/ref is required for type.");
        await EnsureBrowserAsync();
        await _page!.FillAsync(p.Ref, p.Text ?? "", new PageFillOptions { Timeout = 10000 });
        return ToolResult.Ok($"Typed into {p.Ref}");
    }

    private async Task<ToolResult> ScrollAsync(BrowserParameters p)
    {
        await EnsureBrowserAsync();
        var direction = p.Direction?.ToLowerInvariant() ?? "down";
        var delta = direction == "up" ? -500 : 500;
        // Scroll 5 times for visible movement (upstream pattern)
        for (var i = 0; i < 5; i++)
            await _page!.Mouse.WheelAsync(0, delta);
        return await SnapshotAsync();
    }

    private async Task<ToolResult> PressAsync(BrowserParameters p)
    {
        await EnsureBrowserAsync();
        await _page!.Keyboard.PressAsync(p.Key ?? "Enter");
        return ToolResult.Ok($"Pressed {p.Key ?? "Enter"}");
    }

    private async Task<ToolResult> EvaluateAsync(BrowserParameters p)
    {
        if (string.IsNullOrWhiteSpace(p.Expression))
            return ToolResult.Fail("Expression is required for js/evaluate.");
        await EnsureBrowserAsync();
        var result = await _page!.EvaluateAsync<object>(p.Expression);
        return ToolResult.Ok(result?.ToString() ?? "(undefined)");
    }

    /// <summary>
    /// Get the accessibility tree snapshot of the current page.
    /// This is the core representation — no vision model needed.
    /// </summary>
    private async Task<ToolResult> SnapshotAsync()
    {
        if (_page is null) return ToolResult.Fail("No page open. Use navigate first.");

        var title = await _page.TitleAsync();
        var url = _page.Url;

        // Get page text content (Playwright removed Accessibility.SnapshotAsync in newer versions)
        var sb = new StringBuilder();
        sb.AppendLine($"Page: {title}");
        sb.AppendLine($"URL: {url}");
        sb.AppendLine();

        try
        {
            var bodyText = await _page.InnerTextAsync("body");
            sb.AppendLine(bodyText);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"BrowserTool snapshot body extraction failed: {ex}");
            sb.AppendLine("(page content not available)");
        }

        var output = sb.ToString();
        // Truncate if too large (>8000 chars)
        if (output.Length > 8000)
            output = output[..8000] + "\n... [truncated — use click/scroll to explore]";

        return ToolResult.Ok(output);
    }

    private static void RenderAccessibilityNode(
        AccessibilitySnapshotResult node, StringBuilder sb, int depth)
    {
        var indent = new string(' ', depth * 2);
        var role = node.Role ?? "";
        var name = node.Name ?? "";

        // Skip generic/container nodes with no useful info
        if (!string.IsNullOrWhiteSpace(name) || role is "link" or "button" or "textbox"
            or "heading" or "img" or "checkbox" or "radio" or "combobox")
        {
            sb.Append(indent);
            if (!string.IsNullOrWhiteSpace(role))
                sb.Append($"[{role}] ");
            if (!string.IsNullOrWhiteSpace(name))
                sb.Append(name);
            if (!string.IsNullOrWhiteSpace(node.Value))
                sb.Append($" = \"{node.Value}\"");
            sb.AppendLine();
        }

        if (node.Children is not null)
        {
            foreach (var child in node.Children)
                RenderAccessibilityNode(child, sb, depth + 1);
        }
    }

    private async Task<ToolResult> CloseAsync()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
        _page = null;
        _browser = null;
        _playwright = null;
        return ToolResult.Ok("Browser closed.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            try { await _browser.CloseAsync(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"BrowserTool.DisposeAsync close failed: {ex}");
            }
        }
        _playwright?.Dispose();
    }
}

/// <summary>Represents a node in the browser accessibility tree.</summary>
public sealed class AccessibilitySnapshotResult
{
    public string? Role { get; set; }
    public string? Name { get; set; }
    public string? Value { get; set; }
    public IReadOnlyList<AccessibilitySnapshotResult>? Children { get; set; }
}

public sealed class BrowserParameters
{
    public required string Action { get; init; }
    public string? Url { get; init; }
    public string? Ref { get; init; }
    public string? Text { get; init; }
    public string? Direction { get; init; }
    public string? Key { get; init; }
    public string? Expression { get; init; }
}
