namespace Hermes.Agent.Gateway.Platforms;

using System.Net.Http.Json;
using System.Text.Json;
using Hermes.Agent.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// ══════════════════════════════════════════════
// Telegram Bot API Adapter
// ══════════════════════════════════════════════
//
// Uses Telegram Bot API via HTTP long-polling (getUpdates).
// No external NuGet needed — pure HttpClient.
// Upstream ref: gateway/platforms/telegram.py

/// <summary>
/// Telegram platform adapter using Bot API with long-polling.
/// Supports text messages, commands, and media placeholders.
/// </summary>
public sealed class TelegramAdapter : IPlatformAdapter
{
    private readonly string _token;
    private readonly HttpClient _http;
    private readonly ILogger<TelegramAdapter> _logger;
    private Func<MessageEvent, Task<string?>>? _messageHandler;
    private Action<Platform, Exception>? _errorHandler;
    private CancellationTokenSource? _pollCts;
    private long _lastUpdateId;

    public TelegramAdapter(string token, HttpClient? http = null, ILogger<TelegramAdapter>? logger = null)
    {
        _token = token;
        _http = http ?? new HttpClient();
        _logger = logger ?? NullLogger<TelegramAdapter>.Instance;
    }

    public Platform Platform => Platform.Telegram;
    public bool IsConnected { get; private set; }

    private string ApiUrl(string method) => $"https://api.telegram.org/bot{_token}/{method}";

    public async Task<bool> ConnectAsync(CancellationToken ct)
    {
        try
        {
            // Verify token by calling getMe
            var response = await _http.GetAsync(ApiUrl("getMe"), ct);
            if (!response.IsSuccessStatusCode) return false;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.GetProperty("ok").GetBoolean()) return false;

            IsConnected = true;

            // Start polling loop
            _pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = Task.Run(() => PollUpdatesAsync(_pollCts.Token), _pollCts.Token);

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram adapter connect failed");
            return false;
        }
    }

    public async Task<DeliveryResult> SendAsync(OutboundMessage message, CancellationToken ct)
    {
        try
        {
            // Chunk long messages (Telegram limit: 4096 chars)
            var text = message.Text;
            var chunks = ChunkText(text, 4096);

            string? lastMessageId = null;
            foreach (var chunk in chunks)
            {
                var payload = new
                {
                    chat_id = message.ChatId,
                    text = chunk,
                    reply_to_message_id = message.ReplyToMessageId,
                    parse_mode = "Markdown"
                };

                var response = await _http.PostAsJsonAsync(ApiUrl("sendMessage"), payload, ct);
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.GetProperty("ok").GetBoolean())
                {
                    lastMessageId = doc.RootElement.GetProperty("result")
                        .GetProperty("message_id").GetInt64().ToString();
                }
                else
                {
                    var desc = doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() : "Unknown error";
                    return DeliveryResult.Fail($"Telegram API error: {desc}");
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
        BestEffort.Run(() => _pollCts?.Cancel(), _logger, "stopping Telegram polling");
        IsConnected = false;
        await Task.CompletedTask;
    }

    public void SetMessageHandler(Func<MessageEvent, Task<string?>> handler) => _messageHandler = handler;
    public void SetErrorHandler(Action<Platform, Exception> handler) => _errorHandler = handler;

    public ValueTask DisposeAsync()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        return ValueTask.CompletedTask;
    }

    // ── Long-polling loop ──

    private async Task PollUpdatesAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var url = ApiUrl($"getUpdates?offset={_lastUpdateId + 1}&timeout=30");
                var response = await _http.GetAsync(url, ct);
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.GetProperty("ok").GetBoolean()) continue;

                var results = doc.RootElement.GetProperty("result");
                foreach (var update in results.EnumerateArray())
                {
                    _lastUpdateId = update.GetProperty("update_id").GetInt64();

                    if (!update.TryGetProperty("message", out var msg)) continue;
                    if (!msg.TryGetProperty("text", out var textEl)) continue;

                    var text = textEl.GetString() ?? "";
                    var chat = msg.GetProperty("chat");
                    var chatId = chat.GetProperty("id").GetInt64().ToString();
                    var chatType = chat.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : "private";
                    var from = msg.TryGetProperty("from", out var fromEl) ? fromEl : default;
                    var userId = from.ValueKind == JsonValueKind.Object && from.TryGetProperty("id", out var uidEl)
                        ? uidEl.GetInt64().ToString() : "unknown";
                    var username = from.ValueKind == JsonValueKind.Object && from.TryGetProperty("username", out var unEl)
                        ? unEl.GetString() : null;

                    var evt = new MessageEvent
                    {
                        Text = text,
                        Type = text.StartsWith('/') ? MessageType.Command : MessageType.Text,
                        Source = new SessionSource
                        {
                            Platform = Platform.Telegram,
                            ChatId = chatId,
                            UserId = userId,
                            Username = username,
                            IsGroup = chatType is "group" or "supergroup",
                            IsDm = chatType == "private"
                        },
                        MessageId = msg.TryGetProperty("message_id", out var midEl) ? midEl.GetInt64().ToString() : null
                    };

                    if (_messageHandler is not null)
                    {
                        var reply = await _messageHandler(evt);
                        if (!string.IsNullOrWhiteSpace(reply))
                        {
                            await SendAsync(new OutboundMessage
                            {
                                Platform = Platform.Telegram,
                                ChatId = chatId,
                                Text = reply,
                                ReplyToMessageId = evt.MessageId
                            }, ct);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Telegram adapter polling failed; retrying");
                _errorHandler?.Invoke(Platform.Telegram, ex);
                await Task.Delay(5000, ct); // Brief pause before retrying
            }
        }
    }

    private static List<string> ChunkText(string text, int maxLength)
    {
        if (text.Length <= maxLength) return new List<string> { text };

        var chunks = new List<string>();
        var remaining = text.AsSpan();
        while (remaining.Length > 0)
        {
            var len = Math.Min(maxLength, remaining.Length);
            // Try to break at newline
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
