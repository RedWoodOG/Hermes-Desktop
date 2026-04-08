namespace Hermes.Agent.Mcp;

using Hermes.Agent.Core;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using System.Text.Json;

// ══════════════════════════════════════════════
// MCP Server Mode
// ══════════════════════════════════════════════
//
// Upstream ref: mcp_serve.py
// Exposes Hermes tools via MCP protocol so other agents/tools
// can call Hermes as a service.
// Uses HTTP/SSE transport (most compatible).

/// <summary>
/// Exposes registered Hermes tools as an MCP server.
/// Other MCP clients can discover and invoke tools via HTTP.
/// </summary>
public sealed class McpServer : IAsyncDisposable
{
    private readonly ILogger<McpServer> _logger;
    private readonly Dictionary<string, ITool> _tools;
    private readonly string? _authToken;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _running;

    /// <param name="authToken">Bearer token for authentication. If set, all requests must include it. Auto-generated if null.</param>
    public McpServer(IReadOnlyDictionary<string, ITool> tools, ILogger<McpServer> logger, string? authToken = null)
    {
        _tools = new Dictionary<string, ITool>(tools);
        _logger = logger;
        _authToken = authToken ?? Guid.NewGuid().ToString("N");
    }

    /// <summary>Auth token clients must send as Bearer token.</summary>
    public string AuthToken => _authToken!;

    public bool IsRunning => _running;
    public int Port { get; private set; }

    /// <summary>Start the MCP server on the specified port.</summary>
    public async Task StartAsync(int port = 3100, CancellationToken ct = default)
    {
        Port = port;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();

        _running = true;
        _logger.LogInformation("MCP server started on port {Port} with {Count} tools (token: {Token})",
            port, _tools.Count, _authToken![..8] + "...");

        _ = Task.Run(() => ListenLoopAsync(_cts.Token), _cts.Token);
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (_running && !ct.IsCancellationRequested)
        {
            try
            {
                var context = await _listener!.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(context, ct), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "MCP server request error");
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        var method = context.Request.HttpMethod;

        // Auth check — all endpoints require bearer token
        var authHeader = context.Request.Headers["Authorization"];
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.Equals($"Bearer {_authToken}", StringComparison.Ordinal))
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            var unauthorized = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = "Unauthorized — include Authorization: Bearer <token>" }));
            await context.Response.OutputStream.WriteAsync(unauthorized, ct);
            context.Response.Close();
            return;
        }

        try
        {
            var (statusCode, body) = (path, method) switch
            {
                ("/mcp/tools/list", "GET") => HandleToolsList(),
                ("/mcp/tools/call", "POST") => await HandleToolCallAsync(context.Request, ct),
                ("/mcp/info", "GET") => HandleInfo(),
                _ => (404, JsonSerializer.Serialize(new { error = "Not found" }))
            };

            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(body);
            await context.Response.OutputStream.WriteAsync(bytes, ct);
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = 500;
            var error = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = ex.Message }));
            await context.Response.OutputStream.WriteAsync(error, ct);
        }
        finally
        {
            context.Response.Close();
        }
    }

    private (int, string) HandleInfo()
    {
        var info = new
        {
            name = "hermes-desktop",
            version = "1.0.0",
            capabilities = new { tools = true },
            toolCount = _tools.Count
        };
        return (200, JsonSerializer.Serialize(info));
    }

    private (int, string) HandleToolsList()
    {
        var tools = _tools.Values.Select(t => new
        {
            name = t.Name,
            description = t.Description,
            inputSchema = new { type = "object" }
        }).ToArray();

        return (200, JsonSerializer.Serialize(new { tools }));
    }

    private async Task<(int, string)> HandleToolCallAsync(HttpListenerRequest request, CancellationToken ct)
    {
        using var reader = new StreamReader(request.InputStream);
        var body = await reader.ReadToEndAsync(ct);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (!root.TryGetProperty("name", out var nameEl))
            return (400, JsonSerializer.Serialize(new { error = "Missing 'name' field" }));

        var toolName = nameEl.GetString()!;
        if (!_tools.TryGetValue(toolName, out var tool))
            return (404, JsonSerializer.Serialize(new { error = $"Tool '{toolName}' not found" }));

        // Deserialize arguments
        var args = root.TryGetProperty("arguments", out var argsEl)
            ? JsonSerializer.Deserialize(argsEl.GetRawText(), tool.ParametersType)
            : Activator.CreateInstance(tool.ParametersType);

        if (args is null)
            return (400, JsonSerializer.Serialize(new { error = "Failed to parse arguments" }));

        var result = await tool.ExecuteAsync(args, ct);

        var response = new
        {
            content = new[]
            {
                new { type = "text", text = result.Content }
            },
            isError = !result.Success
        };

        return (200, JsonSerializer.Serialize(response));
    }

    public async Task StopAsync()
    {
        _running = false;
        _cts?.Cancel();
        _listener?.Stop();
        _logger.LogInformation("MCP server stopped");
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _listener?.Close();
        _cts?.Dispose();
    }
}
