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

        // Fallback: reasoning models (MiniMax, DeepSeek-R1, etc.) may put text in "reasoning" with empty "content"
        if (string.IsNullOrEmpty(content) &&
            msg.TryGetProperty("reasoning", out var reasoningEl) && reasoningEl.ValueKind == JsonValueKind.String)
        {
            content = reasoningEl.GetString();
        }

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
        // Direct SSE parsing that handles both:
        // 1. <think>...</think> tags in content (QwQ, DeepSeek-R1 via Ollama)
        // 2. Separate "reasoning" JSON field (MiniMax-M2.7, etc.)
        var inThinkBlock = false;
        var contentBuffer = new StringBuilder();

        var payload = BuildPayload(messages, tools: null, stream: true);
        var json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.BaseUrl}/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        HttpResponseMessage? response = null;
        Exception? connectError = null;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex) { response?.Dispose(); connectError = ex; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { response?.Dispose(); connectError = new TimeoutException("Request timed out"); }

        if (connectError is not null)
        {
            yield return new StreamEvent.StreamError(connectError);
            yield break;
        }

        using (response!)
        {
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!ct.IsCancellationRequested)
            {
                string? line;
                IOException? ioError = null;
                try { line = await reader.ReadLineAsync(ct); }
                catch (IOException ioEx) { line = null; ioError = ioEx; }

                if (ioError is not null)
                {
                    yield return new StreamEvent.StreamError(ioError);
                    yield break;
                }

                if (line is null) break;
                if (!line.StartsWith("data: ")) continue;
                var data = line["data: ".Length..];
                if (data == "[DONE]") break;

                JsonDocument? chunk;
                try { chunk = JsonDocument.Parse(data); }
                catch (JsonException) { continue; }

                using (chunk)
                {
                    if (!chunk.RootElement.TryGetProperty("choices", out var choices) ||
                        choices.GetArrayLength() == 0) continue;

                    var delta = choices[0].GetProperty("delta");

                    // Extract reasoning field (MiniMax, DeepSeek-R1 JSON format)
                    if (delta.TryGetProperty("reasoning", out var reasoningEl) &&
                        reasoningEl.ValueKind == JsonValueKind.String)
                    {
                        var reasoning = reasoningEl.GetString();
                        if (!string.IsNullOrEmpty(reasoning))
                            yield return new StreamEvent.ThinkingDelta(reasoning);
                    }

                    // Extract content field
                    if (delta.TryGetProperty("content", out var contentEl) &&
                        contentEl.ValueKind == JsonValueKind.String)
                    {
                        var token = contentEl.GetString();
                        if (!string.IsNullOrEmpty(token))
                        {
                            // Handle <think>...</think> tags within content
                            contentBuffer.Append(token);

                            while (contentBuffer.Length > 0)
                            {
                                var text = contentBuffer.ToString();

                                if (!inThinkBlock)
                                {
                                    var openIdx = text.IndexOf("<think>");
                                    if (openIdx >= 0)
                                    {
                                        if (openIdx > 0)
                                            yield return new StreamEvent.TokenDelta(text[..openIdx]);
                                        inThinkBlock = true;
                                        contentBuffer.Clear();
                                        contentBuffer.Append(text[(openIdx + "<think>".Length)..]);
                                        continue;
                                    }

                                    // Check for partial <think> tag at end
                                    if (text.Length > 0 && text[^1] == '<' || text.EndsWith("<t") ||
                                        text.EndsWith("<th") || text.EndsWith("<thi") ||
                                        text.EndsWith("<thin") || text.EndsWith("<think"))
                                    {
                                        var ps = text.LastIndexOf('<');
                                        if (ps >= 0 && ps < text.Length)
                                        {
                                            if (ps > 0) yield return new StreamEvent.TokenDelta(text[..ps]);
                                            contentBuffer.Clear();
                                            contentBuffer.Append(text[ps..]);
                                            break;
                                        }
                                    }

                                    yield return new StreamEvent.TokenDelta(text);
                                    contentBuffer.Clear();
                                }
                                else
                                {
                                    var closeIdx = text.IndexOf("</think>");
                                    if (closeIdx >= 0)
                                    {
                                        if (closeIdx > 0)
                                            yield return new StreamEvent.ThinkingDelta(text[..closeIdx]);
                                        inThinkBlock = false;
                                        contentBuffer.Clear();
                                        contentBuffer.Append(text[(closeIdx + "</think>".Length)..]);
                                        continue;
                                    }

                                    if (text.EndsWith("<") || text.EndsWith("</") || text.EndsWith("</t") ||
                                        text.EndsWith("</th") || text.EndsWith("</thi") ||
                                        text.EndsWith("</thin") || text.EndsWith("</think"))
                                    {
                                        var ps = text.LastIndexOf('<');
                                        if (ps > 0)
                                        {
                                            yield return new StreamEvent.ThinkingDelta(text[..ps]);
                                            contentBuffer.Clear();
                                            contentBuffer.Append(text[ps..]);
                                            break;
                                        }
                                        break;
                                    }

                                    yield return new StreamEvent.ThinkingDelta(text);
                                    contentBuffer.Clear();
                                }

                                break;
                            }
                        }
                    }
                }
            }
        }

        // Flush remaining content buffer
        if (contentBuffer.Length > 0)
        {
            var remaining = contentBuffer.ToString();
            yield return inThinkBlock
                ? new StreamEvent.ThinkingDelta(remaining)
                : new StreamEvent.TokenDelta(remaining);
        }

        yield return new StreamEvent.MessageComplete("stop", new UsageStats(0, 0));
    }
}
