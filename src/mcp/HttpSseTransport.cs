namespace Hermes.Agent.Mcp;

using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

/// <summary>
/// MCP transport over HTTP/SSE (Server-Sent Events for server→client, HTTP POST for client→server).
/// </summary>
public sealed class HttpSseMcpTransport : IMcpTransport
{
    private readonly McpHttpConfig _config;
    private readonly HttpClient _httpClient;
    private readonly Channel<McpNotification> _notifications = Channel.CreateUnbounded<McpNotification>();
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _readerCts = new();
    private Task? _readerTask;
    private int _requestId = 0;
    private bool _isConnected = false;
    
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
    
    public bool IsConnected => _isConnected;
    
    public IAsyncEnumerable<McpNotification> Notifications => ReadNotificationsAsync();

    public HttpSseMcpTransport(McpHttpConfig config, HttpMessageHandler? handler = null)
    {
        _config = config;
        _httpClient = new HttpClient(handler ?? new HttpClientHandler())
        {
            // A finite global timeout cannot bound a long-lived SSE GET that is
            // designed to stream events for the entire session. With the prior
            // 10-minute value the SSE channel was being torn down on a wall-clock
            // schedule, dropping notifications/tools mid-session (Qodo Reliability
            // finding). The shared HttpClient is therefore unbounded; every
            // request that needs a timeout enforces its own via a linked
            // CancellationTokenSource (see SendRequestAsync for the POST path
            // and the SSE wait CTS for response delivery).
            Timeout = Timeout.InfiniteTimeSpan
        };
        
        // Set headers
        if (config.Headers is not null)
        {
            foreach (var (key, value) in config.Headers)
            {
                _httpClient.DefaultRequestHeaders.Add(key, value);
            }
        }
        
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        // Start SSE connection
        var sseUrl = _config.Url;
        var request = new HttpRequestMessage(HttpMethod.Get, sseUrl);
        
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        
        _isConnected = true;
        
        // Start background reader task for SSE
        _readerTask = Task.Run(() => ReadSseLoop(response, _readerCts.Token), _readerCts.Token);
        
        // Send initialize request via POST
        var initParams = JsonSerializer.SerializeToElement(new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { tools = true, resources = true },
            clientInfo = new { name = "hermes-cs", version = "1.0.0" }
        });
        
        await SendRequestAsync("initialize", initParams, ct);
    }

    /// <summary>
    /// Per-request POST timeout — heavy MCP tools that return their result inline
    /// on the POST response (as opposed to via the SSE channel) can take a long
    /// time, so we use a generous envelope here rather than the prior 2-minute
    /// HttpClient-wide timeout that previously broke them.
    /// </summary>
    private static readonly TimeSpan PostRequestTimeout = TimeSpan.FromMinutes(10);

    public async Task<JsonElement> SendRequestAsync(string method, JsonElement? parameters = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _requestId).ToString();
        var tcs = new TaskCompletionSource<JsonElement>();

        lock (_lock)
        {
            _pendingRequests[id] = tcs;
        }

        var request = new JsonRpcRequest
        {
            Id = id,
            Method = method,
            Params = parameters
        };

        var json = JsonSerializer.Serialize(request, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            // Per-request timeout (HttpClient.Timeout is intentionally infinite for SSE).
            using var postTimeoutCts = new CancellationTokenSource(PostRequestTimeout);
            using var postLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, postTimeoutCts.Token);

            var response = await _httpClient.PostAsync(_config.Url, content, postLinkedCts.Token);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(postLinkedCts.Token);
            var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            
            // Handle immediate response
            if (root.TryGetProperty("id", out var idProp) && idProp.GetString() == id)
            {
                lock (_lock)
                {
                    _pendingRequests.Remove(id);
                }
                
                if (root.TryGetProperty("error", out var errorProp))
                {
                    var error = JsonSerializer.Deserialize<JsonRpcError>(errorProp, JsonOptions);
                    throw new McpException(error?.Message ?? "Unknown error", error?.Code ?? -1);
                }
                
                if (root.TryGetProperty("result", out var resultProp))
                {
                    return resultProp.Clone();
                }
                
                return JsonSerializer.SerializeToElement(new { });
            }
            
            // Response will come via SSE - wait for it
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            
            return await tcs.Task.WaitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                _pendingRequests.Remove(id);
            }
            throw new TimeoutException($"MCP request {method} timed out");
        }
    }

    private async Task ReadSseLoop(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            
            var eventName = "message";
            var eventData = new StringBuilder();
            
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                
                if (string.IsNullOrEmpty(line))
                {
                    // Empty line = end of event
                    if (eventData.Length > 0)
                    {
                        ProcessSseEvent(eventName, eventData.ToString());
                        eventData.Clear();
                        eventName = "message";
                    }
                    continue;
                }
                
                if (line.StartsWith("event:"))
                {
                    eventName = line.Substring(6).Trim();
                }
                else if (line.StartsWith("data:"))
                {
                    eventData.AppendLine(line.Substring(5));
                }
                else if (!line.StartsWith(":"))
                {
                    // Not a comment, treat as data
                    eventData.AppendLine(line);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _isConnected = false;
            _notifications.Writer.TryComplete();
        }
    }

    private void ProcessSseEvent(string eventName, string data)
    {
        try
        {
            var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            
            // Check if this is a response
            if (root.TryGetProperty("id", out var idProp))
            {
                var id = idProp.GetString();
                if (id is not null)
                {
                    TaskCompletionSource<JsonElement>? tcs;
                    lock (_lock)
                    {
                        _pendingRequests.TryGetValue(id, out tcs);
                        _pendingRequests.Remove(id);
                    }
                    
                    if (tcs is not null)
                    {
                        if (root.TryGetProperty("error", out var errorProp))
                        {
                            var error = JsonSerializer.Deserialize<JsonRpcError>(errorProp, JsonOptions);
                            tcs.SetException(new McpException(error?.Message ?? "Unknown error", error?.Code ?? -1));
                        }
                        else if (root.TryGetProperty("result", out var resultProp))
                        {
                            tcs.SetResult(resultProp.Clone());
                        }
                        else
                        {
                            tcs.SetResult(JsonSerializer.SerializeToElement(new { }));
                        }
                    }
                }
            }
            // Check if this is a notification
            else if (root.TryGetProperty("method", out var methodProp))
            {
                var method = methodProp.GetString();
                if (method is not null)
                {
                    JsonElement? @params = null;
                    if (root.TryGetProperty("params", out var paramsProp))
                    {
                        @params = paramsProp.Clone();
                    }
                    
                    _notifications.Writer.TryWrite(new McpNotification(method, @params));
                }
            }
        }
        catch (JsonException)
        {
            // Skip malformed JSON
        }
    }

    private async IAsyncEnumerable<McpNotification> ReadNotificationsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var notification in _notifications.Reader.ReadAllAsync(ct))
        {
            yield return notification;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _readerCts.Cancel();
        _isConnected = false;
        
        try
        {
            if (_readerTask is not null)
                await _readerTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
        
        _httpClient.Dispose();
        _readerCts.Dispose();
    }
}
