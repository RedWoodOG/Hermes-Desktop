namespace Hermes.Agent.Mcp;

using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

/// <summary>
/// MCP transport over WebSocket (bidirectional JSON-RPC).
/// </summary>
public sealed class WebSocketMcpTransport : IMcpTransport
{
    private readonly McpWebSocketConfig _config;
    private readonly ClientWebSocket _webSocket;
    private readonly Channel<McpNotification> _notifications = Channel.CreateUnbounded<McpNotification>();
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _readerCts = new();
    private Task? _readerTask;
    private int _requestId = 0;
    
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
    
    public bool IsConnected => _webSocket.State == WebSocketState.Open;
    
    public IAsyncEnumerable<McpNotification> Notifications => ReadNotificationsAsync();

    public WebSocketMcpTransport(McpWebSocketConfig config)
    {
        _config = config;
        _webSocket = new ClientWebSocket();
        
        // Set headers
        if (config.Headers is not null)
        {
            foreach (var (key, value) in config.Headers)
            {
                _webSocket.Options.SetRequestHeader(key, value);
            }
        }
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _webSocket.ConnectAsync(_config.Url, ct);
        
        // Start background reader task
        _readerTask = Task.Run(() => ReadLoop(_readerCts.Token), _readerCts.Token);
        
        // Send initialize request
        var initParams = JsonSerializer.SerializeToElement(new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { tools = true, resources = true },
            clientInfo = new { name = "hermes-cs", version = "1.0.0" }
        });
        
        await SendRequestAsync("initialize", initParams, ct);
        
        // Send initialized notification
        await SendNotificationAsync("notifications/initialized", null, ct);
    }

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
        var bytes = Encoding.UTF8.GetBytes(json);
        
        await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        
        // Wait for response with timeout
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        
        try
        {
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
    
    private async Task SendNotificationAsync(string method, JsonElement? parameters, CancellationToken ct)
    {
        var notification = new JsonRpcRequest
        {
            Method = method,
            Params = parameters
        };
        
        var json = JsonSerializer.Serialize(notification, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        
        await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var messageBuffer = new List<byte>();
        
        try
        {
            while (!ct.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", ct);
                    break;
                }
                
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    messageBuffer.AddRange(buffer.Take(result.Count));
                    
                    if (result.EndOfMessage)
                    {
                        var json = Encoding.UTF8.GetString(messageBuffer.ToArray());
                        messageBuffer.Clear();
                        
                        ProcessMessage(json);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            _notifications.Writer.TryComplete();
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
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
        
        try
        {
            if (_readerTask is not null)
                await _readerTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
        
        if (_webSocket.State == WebSocketState.Open)
        {
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WebSocketMcpTransport close failed during dispose: {ex}");
            }
        }
        
        _webSocket.Dispose();
        _readerCts.Dispose();
    }
}
