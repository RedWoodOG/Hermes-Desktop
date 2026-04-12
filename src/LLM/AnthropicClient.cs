namespace Hermes.Agent.LLM;

using Hermes.Agent.Core;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

/// <summary>
/// Anthropic Claude API client with streaming support.
/// </summary>
public sealed class AnthropicClient : IChatClient
{
    private readonly HttpClient _httpClient;
    private readonly LlmConfig _config;
    private readonly CredentialPool? _credentialPool;

    private const string ApiVersion = "2023-06-01";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public AnthropicClient(LlmConfig config, HttpClient httpClient, CredentialPool? credentialPool = null)
    {
        _config = config;
        _httpClient = httpClient;
        _credentialPool = credentialPool;

        if (_credentialPool is null && !string.IsNullOrEmpty(config.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("x-api-key", config.ApiKey);
        }

        _httpClient.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }
    
    public async Task<string> CompleteAsync(IEnumerable<Message> messages, CancellationToken ct)
    {
        // Extract system messages to pass as Anthropic's system parameter
        var msgList = messages.ToList();
        var systemPrompt = string.Join("\n", msgList
            .Where(m => m.Role == "system")
            .Select(m => m.Content));
        var nonSystemMessages = msgList.Where(m => m.Role != "system");

        var events = StreamEventsAsync(
            string.IsNullOrEmpty(systemPrompt) ? null : systemPrompt,
            nonSystemMessages, null, ct);
        var content = new StringBuilder();

        await foreach (var evt in events.WithCancellation(ct))
        {
            if (evt is StreamEvent.TokenDelta delta)
            {
                content.Append(delta.Text);
            }
        }

        return content.ToString();
    }

    public async Task<ChatResponse> CompleteWithToolsAsync(
        IEnumerable<Message> messages,
        IEnumerable<ToolDefinition> tools,
        CancellationToken ct)
    {
        var msgList = messages.ToList();
        var systemPrompt = string.Join("\n", msgList
            .Where(m => m.Role == "system")
            .Select(m => m.Content));
        var nonSystemMessages = msgList.Where(m => m.Role != "system");

        var payload = BuildPayload(
            string.IsNullOrEmpty(systemPrompt) ? null : systemPrompt,
            nonSystemMessages, tools, stream: false);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : "stop";
        string? textContent = null;
        List<ToolCall>? toolCalls = null;

        if (root.TryGetProperty("content", out var contentArray))
        {
            var textParts = new StringBuilder();

            foreach (var block in contentArray.EnumerateArray())
            {
                var blockType = block.GetProperty("type").GetString();

                if (blockType == "text")
                {
                    textParts.Append(block.GetProperty("text").GetString() ?? "");
                }
                else if (blockType == "tool_use")
                {
                    toolCalls ??= new List<ToolCall>();
                    toolCalls.Add(new ToolCall
                    {
                        Id = block.GetProperty("id").GetString()!,
                        Name = block.GetProperty("name").GetString()!,
                        Arguments = block.GetProperty("input").GetRawText()
                    });
                }
            }

            textContent = textParts.Length > 0 ? textParts.ToString() : null;
        }

        return new ChatResponse
        {
            Content = textContent,
            ToolCalls = toolCalls,
            FinishReason = stopReason
        };
    }

    public async IAsyncEnumerable<string> StreamAsync(
        IEnumerable<Message> messages,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in StreamEventsAsync(null, messages, null, ct))
        {
            if (evt is StreamEvent.TokenDelta delta)
                yield return delta.Text;
        }
    }

    public IAsyncEnumerable<StreamEvent> StreamAsync(
        string? systemPrompt,
        IEnumerable<Message> messages,
        IEnumerable<ToolDefinition>? tools = null,
        CancellationToken ct = default)
        => StreamEventsAsync(systemPrompt, messages, tools, ct);
    
    private async IAsyncEnumerable<StreamEvent> StreamEventsAsync(
        string? systemPrompt,
        IEnumerable<Message> messages,
        IEnumerable<ToolDefinition>? tools,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var payload = BuildPayload(systemPrompt, messages, tools, stream: true);
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
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
        var cacheCreationTokens = 0;
        var cacheReadTokens = 0;
        
        while (true)
        {
            if (ct.IsCancellationRequested) break;
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                Debug.WriteLine("Anthropic SSE: stream read ended (IOException).");
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (line is null) break;
            if (string.IsNullOrEmpty(line)) continue;
            
            if (!line.StartsWith("data: "))
            {
                // Could be event type line
                continue;
            }
            
            var data = line.Substring(6);
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            
            var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
            
            switch (type)
            {
                case "content_block_start":
                {
                    var block = root.GetProperty("content_block");
                    var blockType = block.GetProperty("type").GetString();
                    var index = root.GetProperty("index").GetInt32();
                    
                    if (blockType == "text")
                    {
                        // Text block started
                    }
                    else if (blockType == "tool_use")
                    {
                        var id = block.GetProperty("id").GetString() ?? $"tool_{index}";
                        var name = block.GetProperty("name").GetString() ?? "";
                        toolUseBuilder[id] = (name, new StringBuilder());
                        yield return new StreamEvent.ToolUseStart(id, name);
                    }
                    break;
                }
                
                case "content_block_delta":
                {
                    var index = root.GetProperty("index").GetInt32();
                    var delta = root.GetProperty("delta");
                    var deltaType = delta.GetProperty("type").GetString();
                    
                    if (deltaType == "text_delta")
                    {
                        var text = delta.GetProperty("text").GetString() ?? "";
                        if (!string.IsNullOrEmpty(text))
                        {
                            yield return new StreamEvent.TokenDelta(text);
                        }
                    }
                    else if (deltaType == "input_json_delta")
                    {
                        var partialJson = delta.GetProperty("partial_json").GetString() ?? "";
                        
                        // Find the tool by index
                        var toolEntry = toolUseBuilder.FirstOrDefault(kvp => 
                        {
                            // Match by order since we don't have index tracking
                            return toolUseBuilder.Keys.ToList().IndexOf(kvp.Key) == index;
                        });
                        
                        if (!string.IsNullOrEmpty(toolEntry.Key))
                        {
                            toolEntry.Value.Json.Append(partialJson);
                            yield return new StreamEvent.ToolUseDelta(toolEntry.Key, partialJson);
                        }
                    }
                    else if (deltaType == "thinking_delta")
                    {
                        var thinking = delta.GetProperty("thinking").GetString() ?? "";
                        if (!string.IsNullOrEmpty(thinking))
                        {
                            yield return new StreamEvent.ThinkingDelta(thinking);
                        }
                    }
                    break;
                }
                
                case "content_block_stop":
                {
                    var index = root.GetProperty("index").GetInt32();
                    
                    // Complete the tool use if this was a tool block
                    var toolEntry = toolUseBuilder.ElementAtOrDefault(index);
                    if (!string.IsNullOrEmpty(toolEntry.Key))
                    {
                        var fullJson = toolEntry.Value.Json.ToString();
                        StreamEvent toolEvt;
                        try
                        {
                            var args = JsonDocument.Parse(fullJson).RootElement;
                            toolEvt = new StreamEvent.ToolUseComplete(
                                toolEntry.Key,
                                toolEntry.Value.Name,
                                args.Clone());
                        }
                        catch (JsonException)
                        {
                            toolEvt = new StreamEvent.StreamError(
                                new JsonException($"Invalid tool arguments: {fullJson}"));
                        }
                        yield return toolEvt;
                    }
                    break;
                }
                
                case "message_delta":
                {
                    var delta = root.GetProperty("delta");
                    var stopReason = delta.TryGetProperty("stop_reason", out var srProp) 
                        ? srProp.GetString() 
                        : null;
                    
                    var usage = root.GetProperty("usage");
                    outputTokens = usage.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
                    
                    if (!string.IsNullOrEmpty(stopReason))
                    {
                        yield return new StreamEvent.MessageComplete(
                            stopReason,
                            new UsageStats(inputTokens, outputTokens, cacheCreationTokens, cacheReadTokens));
                    }
                    break;
                }
                
                case "message_start":
                {
                    var message = root.GetProperty("message");
                    if (message.TryGetProperty("usage", out var usage))
                    {
                        inputTokens = usage.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
                        cacheCreationTokens = usage.TryGetProperty("cache_creation_input_tokens", out var cc) 
                            ? cc.GetInt32() 
                            : 0;
                        cacheReadTokens = usage.TryGetProperty("cache_read_input_tokens", out var cr) 
                            ? cr.GetInt32() 
                            : 0;
                    }
                    break;
                }
                
                case "error":
                {
                    var error = root.GetProperty("error");
                    var message = error.GetProperty("message").GetString() ?? "Unknown error";
                    yield return new StreamEvent.StreamError(new Exception(message));
                    break;
                }
            }
        }
    }
    
    private object BuildPayload(string? systemPrompt, IEnumerable<Message> messages, IEnumerable<ToolDefinition>? tools, bool stream)
    {
        var formattedMessages = new List<object>();
        
        foreach (var msg in messages)
        {
            // Skip system messages — they're passed as the top-level "system" parameter
            if (msg.Role == "system") continue;

            if (msg.Role == "assistant" && msg.ToolCalls is { Count: > 0 })
            {
                // Assistant message with tool calls → content blocks
                var contentBlocks = new List<object>();
                if (!string.IsNullOrEmpty(msg.Content))
                    contentBlocks.Add(new { type = "text", text = msg.Content });
                foreach (var tc in msg.ToolCalls)
                {
                    contentBlocks.Add(new
                    {
                        type = "tool_use",
                        id = tc.Id,
                        name = tc.Name,
                        input = JsonSerializer.Deserialize<JsonElement>(tc.Arguments)
                    });
                }
                formattedMessages.Add(new { role = "assistant", content = contentBlocks });
            }
            else if (msg.Role == "tool")
            {
                // Tool result → user message with tool_result content block
                formattedMessages.Add(new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "tool_result",
                            tool_use_id = msg.ToolCallId ?? "",
                            content = msg.Content
                        }
                    }
                });
            }
            else
            {
                // Regular user/assistant message
                formattedMessages.Add(new
                {
                    role = msg.Role == "assistant" ? "assistant" : "user",
                    content = msg.Content
                });
            }
        }
        
        var payload = new Dictionary<string, object>
        {
            ["model"] = _config.Model,
            ["messages"] = formattedMessages,
            ["max_tokens"] = _config.MaxTokens,
        };
        
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            payload["system"] = systemPrompt;
        }
        
        if (stream)
        {
            payload["stream"] = true;
        }
        
        if (tools is not null)
        {
            payload["tools"] = tools.Select(t => new
            {
                name = t.Name,
                description = t.Description,
                input_schema = t.Parameters
            }).ToList();
        }
        
        return payload;
    }
}
