namespace Hermes.Agent.Mcp;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

/// <summary>
/// MCP transport over stdio (spawn process, communicate via stdin/stdout).
/// </summary>
public sealed class StdioMcpTransport : IMcpTransport
{
    private readonly McpStdioConfig _config;
    private readonly Process _process;
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
    
    public bool IsConnected => _process.HasExited == false;
    
    public IAsyncEnumerable<McpNotification> Notifications => ReadNotificationsAsync();

    public StdioMcpTransport(McpStdioConfig config)
    {
        _config = config;
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = config.Command,
                Arguments = config.Args is null ? "" : string.Join(' ', config.Args),
                WorkingDirectory = config.WorkingDirectory ?? Directory.GetCurrentDirectory(),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardInputEncoding = Encoding.UTF8,
            }
        };
        
        // Set environment variables
        if (config.Env is not null)
        {
            foreach (var (key, value) in config.Env)
            {
                _process.StartInfo.Environment[key] = value;
            }
        }
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _process.Start();
        _process.BeginErrorReadLine();
        
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
        await WriteLineAsync(json, ct);
        
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
        await WriteLineAsync(json, ct);
    }

    private async Task WriteLineAsync(string line, CancellationToken ct)
    {
        await _process.StandardInput.WriteLineAsync(line.AsMemory(), ct);
        await _process.StandardInput.FlushAsync(ct);
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_process.HasExited)
        {
            var line = await _process.StandardOutput.ReadLineAsync(ct);
            if (line is null) break;
            
            if (string.IsNullOrWhiteSpace(line)) continue;
            
            try
            {
                var doc = JsonDocument.Parse(line);
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
                        
                        await _notifications.Writer.WriteAsync(new McpNotification(method, @params), ct);
                    }
                }
            }
            catch (JsonException)
            {
                // Skip malformed JSON
            }
        }
        
        _notifications.Writer.TryComplete();
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
        
        if (!_process.HasExited)
        {
            _process.Kill();
            _process.WaitForExit(5000);
        }
        
        _process.Dispose();
        _readerCts.Dispose();
    }
}

/// <summary>
/// Exception from MCP server.
/// </summary>
public sealed class McpException : Exception
{
    public int Code { get; }
    
    public McpException(string message, int code) : base(message)
    {
        Code = code;
    }
}
