namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// ══════════════════════════════════════════════
// Browser Tool — Accessibility Tree Based
// ══════════════════════════════════════════════
//
// Upstream ref: tools/browser_tool.py
// Uses headless Chromium via Playwright's accessibility snapshot.
// No vision model needed — page represented as text with ref IDs.

/// <summary>
/// Browser automation via accessibility tree (not screenshots).
/// Actions: navigate, click, type, scroll, press, evaluate JS.
/// Elements get refs like @e1, @e2 for agent interaction.
/// </summary>
public sealed class BrowserTool : ITool
{
    public string Name => "browser";
    public string Description => "Browse the web. Actions: navigate(url), click(ref), type(ref,text), scroll(direction), press(key), js(expression), snapshot. Elements have refs like @e1.";
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

        return p.Action.ToLowerInvariant() switch
        {
            "navigate" => await NavigateAsync(p, ct),
            "click" => await RunPlaywrightAsync($"click {p.Ref}", ct),
            "type" or "fill" => await RunPlaywrightAsync($"fill {p.Ref} \"{Escape(p.Text ?? "")}\"", ct),
            "scroll" => await RunPlaywrightAsync($"scroll {p.Direction ?? "down"}", ct),
            "press" => await RunPlaywrightAsync($"press {p.Key ?? "Enter"}", ct),
            "js" or "evaluate" => await RunPlaywrightAsync($"evaluate \"{Escape(p.Expression ?? "")}\"", ct),
            "snapshot" => await RunPlaywrightAsync("snapshot", ct),
            _ => ToolResult.Fail($"Unknown action: {p.Action}. Use: navigate, click, type, scroll, press, js, snapshot")
        };
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

        return await RunPlaywrightAsync($"navigate \"{Escape(p.Url)}\"", ct);
    }

    /// <summary>
    /// Run a Playwright CLI command. Falls back to a helpful error if not installed.
    /// In production, this would use Microsoft.Playwright NuGet directly.
    /// </summary>
    private static async Task<ToolResult> RunPlaywrightAsync(string command, CancellationToken ct)
    {
        try
        {
            // Use npx playwright CLI for accessibility snapshot
            var psi = new ProcessStartInfo
            {
                FileName = "npx",
                Arguments = $"playwright test --headed=false -c \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            var stdout = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderr = await process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);

            if (process.ExitCode == 0)
                return ToolResult.Ok(string.IsNullOrWhiteSpace(stdout) ? "(page loaded)" : stdout);

            return ToolResult.Fail($"Browser action failed: {stderr}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolResult.Fail(
                "Browser tool requires Playwright. Install with: npx playwright install chromium\n" +
                $"Error: {ex.Message}");
        }
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
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
