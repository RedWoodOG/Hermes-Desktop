namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using Microsoft.Playwright;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
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
/// Actions: navigate, click, type, scroll, press, js, snapshot, browser_state, close.
/// Returns page content as structured text, not screenshots.
/// </summary>
public sealed class BrowserTool : ITool, IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Name => "browser";
    public string Description => "Browse the web. Actions: navigate(url), click(selector), type(selector,text), scroll(direction), press(key), js(expression), snapshot, browser_state/state, close.";
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
                "browser_state" or "state" => await BrowserStateAsync(),
                "close" => await CloseAsync(),
                _ => ToolResult.Fail("Unknown action. Use: navigate, click, type, scroll, press, js, snapshot, browser_state/state, close")
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

    private async Task<ToolResult> BrowserStateAsync()
    {
        if (_page is null) return ToolResult.Fail("No page open. Use navigate first.");

        var title = await _page.TitleAsync();
        var url = _page.Url;
        var json = await _page.EvaluateAsync<string>(BrowserStateScript);
        var domState = JsonSerializer.Deserialize<BrowserStateDomSnapshot>(json, JsonOptions)
            ?? new BrowserStateDomSnapshot();

        var viewport = domState.Viewport ?? new BrowserViewport(0, 0);
        var scroll = domState.Scroll ?? new BrowserScrollPosition(0, 0, 0, 0);
        var state = new BrowserStateSnapshot(
            Url: url,
            Title: title,
            Viewport: viewport,
            Scroll: scroll,
            ClickableRefs: domState.ClickableRefs ?? [],
            InputRefs: domState.InputRefs ?? []);

        return ToolResult.Ok(FormatBrowserState(state));
    }

    public static string FormatBrowserState(BrowserStateSnapshot state)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        AppendProperty(sb, "url", state.Url, trailingComma: true, indent: 2);
        AppendProperty(sb, "title", state.Title, trailingComma: true, indent: 2);
        sb.AppendLine($"  viewport: {{ width: {state.Viewport.Width}, height: {state.Viewport.Height} }},");
        sb.AppendLine($"  scroll: {{ x: {state.Scroll.X}, y: {state.Scroll.Y}, maxX: {state.Scroll.MaxX}, maxY: {state.Scroll.MaxY} }},");

        sb.AppendLine("  clickable_refs: [");
        AppendRefs(sb, state.ClickableRefs);
        sb.AppendLine("  ],");

        sb.AppendLine("  input_refs: [");
        AppendRefs(sb, state.InputRefs);
        sb.AppendLine("  ]");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static void AppendRefs(StringBuilder sb, IReadOnlyList<BrowserElementRef> refs)
    {
        if (refs.Count == 0)
        {
            sb.AppendLine("    // none");
            return;
        }

        for (var i = 0; i < refs.Count; i++)
        {
            var item = refs[i];
            sb.Append("    { ");
            AppendInlineProperty(sb, "ref", item.Ref);
            AppendOptionalInlineProperty(sb, "role", item.Role);
            AppendOptionalInlineProperty(sb, "tag", item.Tag);
            AppendOptionalInlineProperty(sb, "type", item.Type);
            AppendOptionalInlineProperty(sb, "name", item.Name);
            AppendOptionalInlineProperty(sb, "placeholder", item.Placeholder);
            AppendOptionalInlineProperty(sb, "text", item.Text);
            AppendOptionalInlineProperty(sb, "value", item.Value);
            sb.Append(" }");
            if (i < refs.Count - 1)
                sb.Append(',');
            sb.AppendLine();
        }
    }

    private static void AppendProperty(StringBuilder sb, string name, string value, bool trailingComma, int indent)
    {
        sb.Append(' ', indent);
        sb.Append(name);
        sb.Append(": ");
        AppendQuoted(sb, value);
        if (trailingComma)
            sb.Append(',');
        sb.AppendLine();
    }

    private static void AppendInlineProperty(StringBuilder sb, string name, string value)
    {
        sb.Append(name);
        sb.Append(": ");
        AppendQuoted(sb, value);
    }

    private static void AppendOptionalInlineProperty(StringBuilder sb, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        sb.Append(", ");
        AppendInlineProperty(sb, name, value);
    }

    private static void AppendQuoted(StringBuilder sb, string? value)
    {
        sb.Append(JsonSerializer.Serialize(Compact(value ?? ""), QuoteOptions));
    }

    private static string Compact(string value)
    {
        var compact = Regex.Replace(value, @"\s+", " ").Trim();
        return compact.Length <= 160 ? compact : compact[..157] + "...";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private static readonly JsonSerializerOptions QuoteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private const string BrowserStateScript =
        """
        () => {
          const compact = (value, max = 120) => String(value || '').replace(/\s+/g, ' ').trim().slice(0, max);
          const escapeAttr = value => String(value || '').replace(/\\/g, '\\\\').replace(/"/g, '\\"');
          const cssEscape = value => {
            if (window.CSS && CSS.escape) return CSS.escape(String(value));
            return String(value).replace(/[^a-zA-Z0-9_-]/g, match => `\\${match}`);
          };
          const isVisible = el => {
            const rect = el.getBoundingClientRect();
            const style = window.getComputedStyle(el);
            return rect.width > 0 &&
              rect.height > 0 &&
              rect.bottom >= 0 &&
              rect.right >= 0 &&
              rect.top <= window.innerHeight &&
              rect.left <= window.innerWidth &&
              style.visibility !== 'hidden' &&
              style.display !== 'none' &&
              style.pointerEvents !== 'none';
          };
          const selectorFor = el => {
            const tag = el.tagName.toLowerCase();
            if (el.id) return `#${cssEscape(el.id)}`;
            for (const attr of ['data-testid', 'data-test', 'name', 'aria-label', 'placeholder', 'title']) {
              const value = el.getAttribute(attr);
              if (value) return `${tag}[${attr}="${escapeAttr(value)}"]`;
            }

            const parts = [];
            let node = el;
            while (node && node.nodeType === Node.ELEMENT_NODE && parts.length < 5) {
              const nodeTag = node.tagName.toLowerCase();
              if (node.id) {
                parts.unshift(`#${cssEscape(node.id)}`);
                break;
              }

              let part = nodeTag;
              const parent = node.parentElement;
              if (parent) {
                const sameTag = Array.from(parent.children).filter(child => child.tagName === node.tagName);
                if (sameTag.length > 1) {
                  part += `:nth-of-type(${sameTag.indexOf(node) + 1})`;
                }
              }
              parts.unshift(part);
              node = parent;
            }

            return parts.join(' > ');
          };
          const describe = el => ({
            ref: selectorFor(el),
            role: compact(el.getAttribute('role')),
            tag: el.tagName.toLowerCase(),
            type: compact(el.getAttribute('type')),
            name: compact(el.getAttribute('name')),
            placeholder: compact(el.getAttribute('placeholder')),
            text: compact(el.innerText || el.getAttribute('aria-label') || el.getAttribute('title') || el.value || el.textContent),
            value: compact(el.matches('input, textarea, select, [contenteditable="true"]') ? (el.value || el.innerText) : '')
          });
          const uniqueVisible = selector => {
            const seen = new Set();
            return Array.from(document.querySelectorAll(selector))
              .filter(isVisible)
              .map(describe)
              .filter(item => {
                if (!item.ref || seen.has(item.ref)) return false;
                seen.add(item.ref);
                return true;
              })
              .slice(0, 50);
          };

          return JSON.stringify({
            viewport: {
              width: Math.round(window.innerWidth),
              height: Math.round(window.innerHeight)
            },
            scroll: {
              x: Math.round(window.scrollX),
              y: Math.round(window.scrollY),
              maxX: Math.max(0, Math.round(document.documentElement.scrollWidth - window.innerWidth)),
              maxY: Math.max(0, Math.round(document.documentElement.scrollHeight - window.innerHeight))
            },
            clickableRefs: uniqueVisible('a[href], button, [role="button"], [role="link"], input[type="button"], input[type="submit"], input[type="reset"], summary, [onclick], [tabindex]:not([tabindex="-1"])'),
            inputRefs: uniqueVisible('input:not([type="hidden"]):not([type="button"]):not([type="submit"]):not([type="reset"]):not([type="image"]), textarea, select, [contenteditable="true"], [role="textbox"], [role="combobox"], [role="searchbox"], [role="spinbutton"]')
          });
        }
        """;

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

public sealed record BrowserStateSnapshot(
    string Url,
    string Title,
    BrowserViewport Viewport,
    BrowserScrollPosition Scroll,
    IReadOnlyList<BrowserElementRef> ClickableRefs,
    IReadOnlyList<BrowserElementRef> InputRefs);

public sealed record BrowserViewport(int Width, int Height);

public sealed record BrowserScrollPosition(int X, int Y, int MaxX, int MaxY);

public sealed record BrowserElementRef(
    string Ref,
    string? Role = null,
    string? Tag = null,
    string? Type = null,
    string? Name = null,
    string? Placeholder = null,
    string? Text = null,
    string? Value = null);

public sealed class BrowserStateDomSnapshot
{
    public BrowserViewport? Viewport { get; set; }
    public BrowserScrollPosition? Scroll { get; set; }
    public IReadOnlyList<BrowserElementRef>? ClickableRefs { get; set; }
    public IReadOnlyList<BrowserElementRef>? InputRefs { get; set; }
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
