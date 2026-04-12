namespace Hermes.Agent.Gateway.Platforms;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Hermes.Agent.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// ══════════════════════════════════════════════
// Discord Bot Adapter
// ══════════════════════════════════════════════
//
// Uses Discord Gateway WebSocket for receiving + REST API for sending.
// No external NuGet needed — pure WebSocket + HttpClient.
// Upstream ref: gateway/platforms/discord.py

/// <summary>
/// Discord platform adapter using Gateway WebSocket + REST API.
/// Supports text messages, commands, and thread replies.
/// </summary>
public sealed class DiscordAdapter : IPlatformAdapter
{
    private readonly string _token;
    private readonly HttpClient _http;
    private readonly ILogger<DiscordAdapter> _logger;
    private ClientWebSocket? _ws;
    private Func<MessageEvent, Task<string?>>? _messageHandler;
    private Action<Platform, Exception>? _errorHandler;
    private CancellationTokenSource? _wsCts;
    private string? _botUserId;
    private int? _heartbeatIntervalMs;
    private int? _lastSequence;

    public DiscordAdapter(string token, HttpClient? http = null, ILogger<DiscordAdapter>? logger = null)
    {
        _token = token;
        _http = http ?? new HttpClient();
        _logger = logger ?? NullLogger<DiscordAdapter>.Instance;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", token);
        _http.DefaultRequestHeaders.Add("User-Agent", "HermesDesktop/1.0");
    }

    public Platform Platform => Platform.Discord;
    public bool IsConnected { get; private set; }

    private const string ApiBase = "https://discord.com/api/v10";
    private const string GatewayUrl = "wss://gateway.discord.gg/?v=10&encoding=json";

    public async Task<bool> ConnectAsync(CancellationToken ct)
    {
        try
        {
            // Verify token by calling /users/@me
            var response = await _http.GetAsync($"{ApiBase}/users/@me", ct);
            if (!response.IsSuccessStatusCode) return false;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            _botUserId = doc.RootElement.GetProperty("id").GetString();

            // Connect WebSocket
            _ws = new ClientWebSocket();
            _wsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await _ws.ConnectAsync(new Uri(GatewayUrl), _wsCts.Token);

            // Receive Hello and start heartbeat
            var hello = await ReceiveJsonAsync(_wsCts.Token);
            if (hello is not null && hello.RootElement.TryGetProperty("d", out var helloData))
                _heartbeatIntervalMs = helloData.GetProperty("heartbeat_interval").GetInt32();

            // Send Identify
            var identify = new
            {
                op = 2,
                d = new
                {
                    token = _token,
                    intents = 512 | 32768, // GUILD_MESSAGES | MESSAGE_CONTENT
                    properties = new { os = "windows", browser = "hermes", device = "hermes" }
                }
            };
            await SendJsonAsync(identify, _wsCts.Token);

            IsConnected = true;

            // Start receive loop and heartbeat
            _ = Task.Run(() => HeartbeatLoopAsync(_wsCts.Token), _wsCts.Token);
            _ = Task.Run(() => ReceiveLoopAsync(_wsCts.Token), _wsCts.Token);

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discord adapter connect failed");
            return false;
        }
    }

    public async Task<DeliveryResult> SendAsync(OutboundMessage message, CancellationToken ct)
    {
        try
        {
            // Discord limit: 2000 chars
            var chunks = ChunkText(message.Text, 2000);

            string? lastMessageId = null;
            foreach (var chunk in chunks)
            {
                var payload = new { content = chunk };
                var response = await _http.PostAsJsonAsync(
                    $"{ApiBase}/channels/{message.ChatId}/messages", payload, ct);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    lastMessageId = doc.RootElement.GetProperty("id").GetString();
                }
                else
                {
                    var error = await response.Content.ReadAsStringAsync(ct);
                    return DeliveryResult.Fail($"Discord API error: {response.StatusCode} — {error}");
                }
            }

            return DeliveryResult.Ok(lastMessageId);
        }
        catch (Exception ex)
        {
            return DeliveryResult.Fail(ex.Message);
        }
    }

    public async Task DisconnectAsync()
    {
        _wsCts?.Cancel();
        if (_ws?.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None); }
            catch (Exception ex)
            {
                BestEffort.LogFailure(_logger, ex, "closing Discord WebSocket during shutdown");
            }
        }
        IsConnected = false;
    }

    public void SetMessageHandler(Func<MessageEvent, Task<string?>> handler) => _messageHandler = handler;
    public void SetErrorHandler(Action<Platform, Exception> handler) => _errorHandler = handler;

    public ValueTask DisposeAsync()
    {
        _wsCts?.Cancel();
        _wsCts?.Dispose();
        _ws?.Dispose();
        return ValueTask.CompletedTask;
    }

    // ── WebSocket receive loop ──

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            try
            {
                var doc = await ReceiveJsonAsync(ct);
                if (doc is null) continue;

                var root = doc.RootElement;
                if (root.TryGetProperty("s", out var seqEl) && seqEl.ValueKind == JsonValueKind.Number)
                    _lastSequence = seqEl.GetInt32();

                var op = root.GetProperty("op").GetInt32();
                if (op != 0) continue; // Only handle Dispatch (op=0)

                var eventName = root.TryGetProperty("t", out var tEl) ? tEl.GetString() : null;
                if (eventName != "MESSAGE_CREATE") continue;

                var d = root.GetProperty("d");
                var authorId = d.GetProperty("author").GetProperty("id").GetString();

                // Ignore own messages
                if (authorId == _botUserId) continue;

                var content = d.TryGetProperty("content", out var contentEl) ? contentEl.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(content)) continue;

                var channelId = d.GetProperty("channel_id").GetString()!;
                var guildId = d.TryGetProperty("guild_id", out var guildEl) ? guildEl.GetString() : null;
                var username = d.GetProperty("author").TryGetProperty("username", out var unEl) ? unEl.GetString() : null;

                var evt = new MessageEvent
                {
                    Text = content,
                    Type = content.StartsWith('/') ? MessageType.Command : MessageType.Text,
                    Source = new SessionSource
                    {
                        Platform = Platform.Discord,
                        ChatId = channelId,
                        UserId = authorId!,
                        Username = username,
                        IsGroup = guildId is not null,
                        IsDm = guildId is null
                    },
                    MessageId = d.GetProperty("id").GetString()
                };

                if (_messageHandler is not null)
                {
                    var reply = await _messageHandler(evt);
                    if (!string.IsNullOrWhiteSpace(reply))
                    {
                        await SendAsync(new OutboundMessage
                        {
                            Platform = Platform.Discord,
                            ChatId = channelId,
                            Text = reply
                        }, ct);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Discord adapter receive loop failed");
                _errorHandler?.Invoke(Platform.Discord, ex);
            }
        }
    }

    // ── Heartbeat ──

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        var interval = _heartbeatIntervalMs ?? 45000;
        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            await Task.Delay(interval, ct);
            var heartbeat = new { op = 1, d = _lastSequence };
            try { await SendJsonAsync(heartbeat, ct); }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                BestEffort.LogFailure(_logger, ex, "sending Discord heartbeat");
                break;
            }
        }
    }

    // ── WebSocket helpers ──

    private async Task<JsonDocument?> ReceiveJsonAsync(CancellationToken ct)
    {
        var buffer = new byte[16384];
        var sb = new StringBuilder();

        WebSocketReceiveResult result;
        do
        {
            result = await _ws!.ReceiveAsync(buffer, ct);
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        } while (!result.EndOfMessage);

        if (result.MessageType == WebSocketMessageType.Close)
            return null;

        return JsonDocument.Parse(sb.ToString());
    }

    private async Task SendJsonAsync(object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws!.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static List<string> ChunkText(string text, int maxLength)
    {
        if (text.Length <= maxLength) return new List<string> { text };

        var chunks = new List<string>();
        var remaining = text.AsSpan();
        while (remaining.Length > 0)
        {
            var len = Math.Min(maxLength, remaining.Length);
            if (len < remaining.Length)
            {
                var lastNewline = remaining[..len].LastIndexOf('\n');
                if (lastNewline > len / 2) len = lastNewline + 1;
            }
            chunks.Add(remaining[..len].ToString());
            remaining = remaining[len..];
        }
        return chunks;
    }
}
