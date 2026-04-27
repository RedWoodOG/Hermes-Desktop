namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using System.Net.Http.Json;
using System.Text.Json;

// ══════════════════════════════════════════════
// OSV Security Check Tool
// ══════════════════════════════════════════════
//
// Upstream ref: tools/osv_check.py
// Queries OSV.dev API for known vulnerabilities in packages.

/// <summary>
/// Check packages for known security vulnerabilities via OSV.dev API.
/// </summary>
public sealed class OsvTool : ITool
{
    public string Name => "osv_check";
    public string Description => "Check a package for known security vulnerabilities. Provide package name, version, and ecosystem (npm, pypi, crates.io, etc).";
    public Type ParametersType => typeof(OsvParameters);

    public async Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (OsvParameters)parameters;

        if (string.IsNullOrWhiteSpace(p.Package))
            return ToolResult.Fail("Package name is required.");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

            var payload = new
            {
                package = new
                {
                    name = p.Package,
                    ecosystem = p.Ecosystem ?? "npm"
                },
                version = p.Version
            };

            var response = await http.PostAsJsonAsync("https://api.osv.dev/v1/query", payload, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return ToolResult.Fail($"OSV API error: {response.StatusCode}");

            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("vulns", out var vulns) ||
                vulns.GetArrayLength() == 0)
            {
                var versionInfo = p.Version is not null ? $"@{p.Version}" : "";
                return ToolResult.Ok($"No known vulnerabilities for {p.Package}{versionInfo} ({p.Ecosystem ?? "npm"}).");
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Found {vulns.GetArrayLength()} vulnerability(ies) for {p.Package}:");

            foreach (var vuln in vulns.EnumerateArray().Take(10))
            {
                var id = vuln.GetProperty("id").GetString();
                var summary = vuln.TryGetProperty("summary", out var sumEl) ? sumEl.GetString() : "No summary";
                var severity = vuln.TryGetProperty("database_specific", out var dbEl) &&
                    dbEl.TryGetProperty("severity", out var sevEl) ? sevEl.GetString() : "UNKNOWN";

                sb.AppendLine($"\n  [{severity}] {id}");
                sb.AppendLine($"  {summary}");
            }

            return ToolResult.Ok(sb.ToString());
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"OSV check failed: {ex.Message}");
        }
    }
}

public sealed class OsvParameters
{
    public required string Package { get; init; }
    public string? Version { get; init; }
    public string? Ecosystem { get; init; }
}
