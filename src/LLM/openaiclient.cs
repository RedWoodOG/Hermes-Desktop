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
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public OpenAiClient(LlmConfig config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;

        if (!string.IsNullOrEmpty(config.ApiKey))
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

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data: ")) continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            using var chunk = JsonDocument.Parse(data);
            var choices = chunk.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0) continue;

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
            return new { role = m.Role, content = m.Content, tool_calls = (object?)null };
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
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync($"{_config.BaseUrl}/chat/completions", content, ct);
        response.EnsureSuccessStatusCode();
        return response;
    }
}
