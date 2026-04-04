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
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync($"{_config.BaseUrl}/chat/completions", content, ct);
        response.EnsureSuccessStatusCode();
        return response;
    }
    
    public IAsyncEnumerable<StreamEvent> StreamAsync(IEnumerable<Message> messages, CancellationToken ct = default)
        => StreamAsync(null, messages, null, ct);
    
    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        string? systemPrompt,
        IEnumerable<Message> messages,
        IEnumerable<ToolDefinition>? tools,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messageList = messages.ToList();
        
        var payload = BuildPayload(systemPrompt, messageList, tools, stream: true);
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.BaseUrl}/chat/completions")
        {
            Content = content
        };
        
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        
        var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        
        var toolUseBuilder = new Dictionary<string, (string Name, StringBuilder Json)>();
        var inputTokens = 0;
        var outputTokens = 0;
        
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line)) continue;
            if (!line.StartsWith("data: ")) continue;
            
            var data = line.Substring(6);
            if (data == "[DONE]") break;
            
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                continue;
            
            var choice = choices[0];
            var delta = choice.GetProperty("delta");
            
            // Handle content
            if (delta.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
            {
                var text = contentProp.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    yield return new StreamEvent.TokenDelta(text);
                }
            }
            
            // Handle tool calls (OpenAI format)
            if (delta.TryGetProperty("tool_calls", out var toolCalls))
            {
                foreach (var tc in toolCalls.EnumerateArray())
                {
                    var index = tc.GetProperty("index").GetInt32();
                    var id = tc.TryGetProperty("id", out var idProp) ? idProp.GetString() : $"tool_{index}";
                    var function = tc.GetProperty("function");
                    var name = function.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : "";
                    var args = function.TryGetProperty("arguments", out var argsProp) ? argsProp.GetString() : "";
                    
                    if (!string.IsNullOrEmpty(name))
                    {
                        toolUseBuilder[id!] = (name, new StringBuilder());
                        yield return new StreamEvent.ToolUseStart(id!, name);
                    }
                    
                    if (!string.IsNullOrEmpty(args) && toolUseBuilder.TryGetValue(id!, out var builder))
                    {
                        builder.Json.Append(args);
                        yield return new StreamEvent.ToolUseDelta(id!, args);
                    }
                }
            }
            
            // Handle finish reason
            if (choice.TryGetProperty("finish_reason", out var finishProp) && finishProp.ValueKind == JsonValueKind.String)
            {
                var finishReason = finishProp.GetString();
                
                // Complete any pending tool uses
                foreach (var (id, (name, jsonBuilder)) in toolUseBuilder)
                {
                    var fullJson = jsonBuilder.ToString();
                    StreamEvent toolEvt;
                    try
                    {
                        var args = JsonDocument.Parse(fullJson).RootElement;
                        toolEvt = new StreamEvent.ToolUseComplete(id, name, args.Clone());
                    }
                    catch (JsonException)
                    {
                        toolEvt = new StreamEvent.StreamError(new JsonException($"Invalid tool arguments: {fullJson}"));
                    }
                    yield return toolEvt;
                }
                
                // Get usage if available
                if (root.TryGetProperty("usage", out var usage))
                {
                    inputTokens = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
                    outputTokens = usage.TryGetProperty("completion_tokens", out var ct2) ? ct2.GetInt32() : 0;
                }
                
                yield return new StreamEvent.MessageComplete(
                    finishReason ?? "stop",
                    new UsageStats(inputTokens, outputTokens));
            }
        }
    }
    
    private object BuildPayload(string? systemPrompt, List<Message> messages, IEnumerable<ToolDefinition>? tools, bool stream)
    {
        var formattedMessages = new List<object>();
        
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            formattedMessages.Add(new { role = "system", content = systemPrompt });
        }
        
        foreach (var msg in messages)
        {
            formattedMessages.Add(new { role = msg.Role, content = msg.Content });
        }
        
        var payload = new Dictionary<string, object>
        {
            ["model"] = _config.Model,
            ["messages"] = formattedMessages,
            ["temperature"] = _config.Temperature,
            ["max_tokens"] = _config.MaxTokens,
        };
        
        if (stream)
        {
            payload["stream"] = true;
        }
        
        if (tools is not null)
        {
            payload["tools"] = tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = t.InputSchema
                }
            }).ToList();
        }
        
        return payload;
    }
}
