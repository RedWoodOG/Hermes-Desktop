namespace Hermes.Agent.LLM;

using Hermes.Agent.Core;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

public sealed class OpenAiClient : IChatClient
{
    private readonly HttpClient _httpClient;
    private readonly LlmConfig _config;
    private readonly CredentialPool? _credentialPool;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public OpenAiClient(LlmConfig config, HttpClient httpClient, CredentialPool? credentialPool = null)
    {
        _config = config;
        _httpClient = httpClient;
        _credentialPool = credentialPool;

        if (_credentialPool is null && !string.IsNullOrEmpty(config.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.ApiKey);
        }
    }

    // ── Simple completion (backwards compatible) ──

    public async Task<string> CompleteAsync(IEnumerable<Message> messages, CancellationToken ct)
    {
        var payload = BuildPayload(messages, tools: null, stream: false);
        using var response = await PostAsync(payload, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("choices")[0]
                  .GetProperty("message")
                  .GetProperty("content").GetString() ?? "";
    }

    // ── Completion with tool calling ──

    public async Task<ChatResponse> CompleteWithToolsAsync(
        IEnumerable<Message> messages,
        IEnumerable<ToolDefinition> tools,
        CancellationToken ct)
    {
        var toolDefs = tools.Select(t => new
        {
            type = "function",
            function = new { name = t.Name, description = t.Description, parameters = t.Parameters }
        }).ToArray();

        var payload = BuildPayload(messages, toolDefs, stream: false);
        using var response = await PostAsync(payload, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        var choice = doc.RootElement.GetProperty("choices")[0];
        var msg = choice.GetProperty("message");
        var finishReason = choice.GetProperty("finish_reason").GetString();

        string? content = null;
        if (msg.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
            content = contentEl.GetString();

        List<ToolCall>? toolCalls = null;
        if (msg.TryGetProperty("tool_calls", out var toolCallsEl) && toolCallsEl.ValueKind == JsonValueKind.Array)
        {
            toolCalls = new List<ToolCall>();
            foreach (var tc in toolCallsEl.EnumerateArray())
            {
                var fn = tc.GetProperty("function");
                toolCalls.Add(new ToolCall
                {
                    Id = tc.GetProperty("id").GetString()!,
                    Name = fn.GetProperty("name").GetString()!,
                    Arguments = fn.GetProperty("arguments").GetString() ?? "{}"
                });
            }
        }

        return new ChatResponse { Content = content, ToolCalls = toolCalls, FinishReason = finishReason };
    }

    // ── Streaming completion ──

    public async IAsyncEnumerable<string> StreamAsync(
        IEnumerable<Message> messages,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var payload = BuildPayload(messages, tools: null, stream: true);
        var json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.BaseUrl}/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        HttpResponseMessage? response = null;
        string? connectionError = null;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            response?.Dispose();
            connectionError = $"\n[Connection error: {ex.Message}]";
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            response?.Dispose();
            connectionError = "\n[Request timed out — the LLM server may be overloaded or unreachable]";
        }

        if (connectionError is not null)
        {
            yield return connectionError;
            yield break;
        }

        using (response!)
        {
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!ct.IsCancellationRequested)
            {
                string? line;
                string? readError = null;
                try
                {
                    line = await reader.ReadLineAsync(ct);
                }
                catch (IOException)
                {
                    readError = "\n[Connection lost during streaming]";
                    line = null;
                }

                if (readError is not null)
                {
                    yield return readError;
                    yield break;
                }

                if (line is null) break;
                if (!line.StartsWith("data: ")) continue;

                var data = line["data: ".Length..];
                if (data == "[DONE]") break;

                JsonDocument? chunk;
                try
                {
                    chunk = JsonDocument.Parse(data);
                }
                catch (JsonException)
                {
                    continue; // Skip malformed chunks
                }

                using (chunk)
                {
                    if (!chunk.RootElement.TryGetProperty("choices", out var choices) ||
                        choices.GetArrayLength() == 0) continue;

                    var delta = choices[0].GetProperty("delta");
                    if (delta.TryGetProperty("content", out var contentEl) &&
                        contentEl.ValueKind == JsonValueKind.String)
                    {
                        var token = contentEl.GetString();
                        if (!string.IsNullOrEmpty(token))
                            yield return token;
                    }
                }
            }
        }
    }

    // ── Helpers ──

    private object BuildPayload(IEnumerable<Message> messages, object? tools, bool stream)
    {
        var msgs = messages.Select(m =>
        {
            // Tool result message
            if (m.Role == "tool")
                return (object)new { role = "tool", content = m.Content, tool_call_id = m.ToolCallId };

            // Assistant message with tool calls
            if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 })
                return new
                {
                    role = "assistant",
                    content = m.Content ?? (object?)null,
                    tool_calls = m.ToolCalls.Select(tc => new
                    {
                        id = tc.Id,
                        type = "function",
                        function = new { name = tc.Name, arguments = tc.Arguments }
                    }).ToArray()
                };

            // Regular message
            return (object)new { role = m.Role, content = m.Content };
        }).ToArray();

        if (tools is not null)
        {
            return new
            {
                model = _config.Model,
                messages = msgs,
                tools,
                tool_choice = "auto",
                temperature = 0.7,
                stream
            };
        }

        return new
        {
            model = _config.Model,
            messages = msgs,
            temperature = 0.7,
            stream
        };
    }

    private async Task<HttpResponseMessage> PostAsync(object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        var url = $"{_config.BaseUrl}/chat/completions";

        // If credential pool is available, use it with retry on 401
        if (_credentialPool is not null && _credentialPool.HasHealthyCredentials)
        {
            const int maxRetries = 3;
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                var apiKey = _credentialPool.GetNext();
                if (apiKey is null) break;

                var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

                var response = await _httpClient.SendAsync(request, ct);
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                    response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    _credentialPool.MarkFailed(apiKey);
                    response.Dispose();
                    continue; // Retry with next key
                }

                response.EnsureSuccessStatusCode();
                return response;
            }
        }

        // Fallback: use default header auth
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var fallbackResponse = await _httpClient.PostAsync(url, content, ct);
        fallbackResponse.EnsureSuccessStatusCode();
        return fallbackResponse;
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        string? systemPrompt,
        IEnumerable<Message> messages,
        IEnumerable<ToolDefinition>? tools = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Buffer-based <think>...</think> parser that handles tags split across SSE tokens.
        // Ollama reasoning models (QwQ, DeepSeek-R1, etc.) wrap reasoning in these tags.
        var inThinkBlock = false;
        var buffer = new StringBuilder();

        await foreach (var token in StreamAsync(messages.ToList(), ct))
        {
            // Detect error markers from the base stream (connection errors, timeouts)
            // and route them as StreamError instead of TokenDelta
            if (token.StartsWith("\n[") && token.EndsWith("]"))
            {
                yield return new StreamEvent.StreamError(new Exception(token.Trim('\n', '[', ']')));
                continue;
            }

            buffer.Append(token);

            // Process buffer — emit complete segments, keep partial tags buffered
            while (buffer.Length > 0)
            {
                var text = buffer.ToString();

                if (!inThinkBlock)
                {
                    var openIdx = text.IndexOf("<think>");
                    if (openIdx >= 0)
                    {
                        // Emit text before the tag as regular content
                        if (openIdx > 0)
                            yield return new StreamEvent.TokenDelta(text[..openIdx]);
                        inThinkBlock = true;
                        buffer.Clear();
                        buffer.Append(text[(openIdx + "<think>".Length)..]);
                        continue; // Re-process remaining buffer
                    }

                    // Check for partial tag at end (e.g., "<thi" could become "<think>")
                    if ((text.Length > 0 && text[^1] == '<') || (text.Length >= 2 && text.EndsWith("<t")) ||
                        text.EndsWith("<th") || text.EndsWith("<thi") || text.EndsWith("<thin") || text.EndsWith("<think"))
                    {
                        // Find where the potential partial tag starts
                        var partialStart = text.LastIndexOf('<');
                        if (partialStart >= 0 && partialStart < text.Length)
                        {
                            if (partialStart > 0)
                                yield return new StreamEvent.TokenDelta(text[..partialStart]);
                            buffer.Clear();
                            buffer.Append(text[partialStart..]);
                            break; // Wait for more data
                        }
                    }

                    // No tags — emit everything
                    yield return new StreamEvent.TokenDelta(text);
                    buffer.Clear();
                }
                else
                {
                    // Inside think block — look for closing tag
                    var closeIdx = text.IndexOf("</think>");
                    if (closeIdx >= 0)
                    {
                        if (closeIdx > 0)
                            yield return new StreamEvent.ThinkingDelta(text[..closeIdx]);
                        inThinkBlock = false;
                        buffer.Clear();
                        buffer.Append(text[(closeIdx + "</think>".Length)..]);
                        continue; // Re-process remaining buffer (may contain another <think>)
                    }

                    // Check for partial closing tag at end
                    if (text.EndsWith("<") || text.EndsWith("</") || text.EndsWith("</t") ||
                        text.EndsWith("</th") || text.EndsWith("</thi") || text.EndsWith("</thin") ||
                        text.EndsWith("</think"))
                    {
                        var partialStart = text.LastIndexOf('<');
                        if (partialStart > 0)
                        {
                            yield return new StreamEvent.ThinkingDelta(text[..partialStart]);
                            buffer.Clear();
                            buffer.Append(text[partialStart..]);
                            break; // Wait for more data
                        }
                        break; // Entire buffer is partial tag — wait
                    }

                    // No closing tag — emit as thinking
                    yield return new StreamEvent.ThinkingDelta(text);
                    buffer.Clear();
                }

                break; // Processed entire buffer
            }
        }

        // Flush any remaining buffer
        if (buffer.Length > 0)
        {
            var remaining = buffer.ToString();
            if (inThinkBlock)
                yield return new StreamEvent.ThinkingDelta(remaining);
            else
                yield return new StreamEvent.TokenDelta(remaining);
        }

        yield return new StreamEvent.MessageComplete("stop", new UsageStats(0, 0));
    }
}
