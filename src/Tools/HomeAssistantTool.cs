namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

// ══════════════════════════════════════════════
// Home Assistant Tool
// ══════════════════════════════════════════════
//
// Upstream ref: tools/homeassistant_tool.py
// REST API: list entities, get state, list services, call services.
// Blocks dangerous domains (shell commands, scripts).

/// <summary>
/// Control Home Assistant smart home devices.
/// Actions: list_entities, get_state, list_services, call_service.
/// </summary>
public sealed class HomeAssistantTool : ITool
{
    private static readonly HashSet<string> BlockedDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "shell_command", "script", "automation", "homeassistant"
    };

    public string Name => "home_assistant";
    public string Description => "Control Home Assistant. Actions: list_entities, get_state(entity_id), call_service(domain, service, entity_id).";
    public Type ParametersType => typeof(HomeAssistantParameters);

    public async Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (HomeAssistantParameters)parameters;

        var baseUrl = Environment.GetEnvironmentVariable("HA_URL") ?? "http://localhost:8123";
        var token = Environment.GetEnvironmentVariable("HA_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            return ToolResult.Fail("Home Assistant token not configured. Set HA_TOKEN environment variable.");

        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            return p.Action.ToLowerInvariant() switch
            {
                "list_entities" => await ListEntitiesAsync(http, ct),
                "get_state" => await GetStateAsync(http, p.EntityId, ct),
                "call_service" => await CallServiceAsync(http, p.Domain, p.Service, p.EntityId, p.Data, ct),
                "list_services" => await ListServicesAsync(http, ct),
                _ => ToolResult.Fail("Unknown action. Use: list_entities, get_state, call_service, list_services")
            };
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Home Assistant error: {ex.Message}");
        }
    }

    private static async Task<ToolResult> ListEntitiesAsync(HttpClient http, CancellationToken ct)
    {
        var response = await http.GetStringAsync("/api/states", ct);
        using var doc = JsonDocument.Parse(response);
        var entities = doc.RootElement.EnumerateArray()
            .Select(e => $"{e.GetProperty("entity_id").GetString()}: {e.GetProperty("state").GetString()}")
            .Take(50);
        return ToolResult.Ok(string.Join("\n", entities));
    }

    private static async Task<ToolResult> GetStateAsync(HttpClient http, string? entityId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entityId))
            return ToolResult.Fail("entity_id is required for get_state.");
        var response = await http.GetStringAsync($"/api/states/{entityId}", ct);
        return ToolResult.Ok(response);
    }

    private static async Task<ToolResult> CallServiceAsync(
        HttpClient http, string? domain, string? service, string? entityId,
        Dictionary<string, string>? data, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(service))
            return ToolResult.Fail("domain and service are required for call_service.");
        if (BlockedDomains.Contains(domain))
            return ToolResult.Fail($"Domain '{domain}' is blocked for security.");

        var payload = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(entityId))
            payload["entity_id"] = entityId;
        if (data is not null)
            foreach (var (k, v) in data) payload[k] = v;

        var response = await http.PostAsJsonAsync($"/api/services/{domain}/{service}", payload, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return response.IsSuccessStatusCode
            ? ToolResult.Ok($"Service {domain}.{service} called successfully.")
            : ToolResult.Fail($"Service call failed: {body}");
    }

    private static async Task<ToolResult> ListServicesAsync(HttpClient http, CancellationToken ct)
    {
        var response = await http.GetStringAsync("/api/services", ct);
        using var doc = JsonDocument.Parse(response);
        var services = doc.RootElement.EnumerateArray()
            .Select(s => s.GetProperty("domain").GetString())
            .Distinct()
            .Take(30);
        return ToolResult.Ok($"Available domains:\n{string.Join("\n", services)}");
    }
}

public sealed class HomeAssistantParameters
{
    public required string Action { get; init; }
    public string? EntityId { get; init; }
    public string? Domain { get; init; }
    public string? Service { get; init; }
    public Dictionary<string, string>? Data { get; init; }
}
