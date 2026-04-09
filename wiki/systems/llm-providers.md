---
title: LLM Providers
type: system
tags: [llm, streaming, credentials]
created: 2026-04-09
updated: 2026-04-09
sources: [src/LLM/ichatclient.cs, src/LLM/openaiclient.cs, src/LLM/AnthropicClient.cs, src/LLM/CredentialPool.cs, src/LLM/StreamTypes.cs, src/LLM/ModelRouter.cs]
---

# LLM Providers

Two IChatClient implementations: OpenAiClient (OpenAI-compatible, also used for Ollama/MiniMax/DeepSeek) and AnthropicClient (Claude API).

## IChatClient Interface

Four methods, all accepting CancellationToken:
1. `CompleteAsync(messages) -> string` -- simple text completion
2. `CompleteWithToolsAsync(messages, tools) -> ChatResponse` -- returns Content + ToolCalls + FinishReason
3. `StreamAsync(messages) -> IAsyncEnumerable<string>` -- token-by-token
4. `StreamAsync(systemPrompt, messages, tools?) -> IAsyncEnumerable<StreamEvent>` -- structured events

## OpenAiClient

- Endpoint: `{BaseUrl}/chat/completions`
- Tool format: `{ type: "function", function: { name, description, parameters } }`
- Reasoning model support: falls back to "reasoning" JSON field when "content" is empty (MiniMax-M2.7, DeepSeek-R1)
- Streaming: parses `<think>...</think>` tags in content (QwQ, DeepSeek-R1 via Ollama) and emits ThinkingDelta events
- Credential pool integration: retries up to 3x on 401/403/429 with rotation

## AnthropicClient

- Endpoint: `https://api.anthropic.com/v1/messages`
- API version: `2023-06-01`
- System messages extracted and passed as top-level `system` parameter
- Tool calls use content blocks: `tool_use` (outgoing), `tool_result` as user messages (incoming)
- Streaming: handles `content_block_start/delta/stop`, `thinking_delta`, `message_delta` SSE events
- Tracks cache tokens: `cache_creation_input_tokens`, `cache_read_input_tokens`

## StreamEvent Hierarchy

```
StreamEvent (abstract record)
  TokenDelta(Text)         -- regular text token
  ThinkingDelta(Text)      -- reasoning/thinking content
  ToolUseStart(Id, Name)   -- tool call begins
  ToolUseDelta(Id, Json)   -- partial tool JSON
  ToolUseComplete(Id, Name, Arguments)  -- full tool call
  MessageComplete(StopReason, Usage?)   -- stream done
  StreamError(Exception)   -- error during stream
```

## CredentialPool

Thread-safe multi-key manager (`src/LLM/CredentialPool.cs`):
- Strategies: LeastUsed (default), RoundRobin, Random, FillFirst
- Cooldowns: 1 hour for 429 (rate limit), 24 hours for auth errors
- Lease system: `AcquireLease`/`ReleaseLease` for concurrent access with soft cap
- `MarkFailed(apiKey, statusCode, reason)` -- marks credential as failed, starts cooldown
- `HasHealthyCredentials` -- checks if any credentials are available

## ModelRouter

Smart routing for cost savings (disabled by default):
- 32 complexity keywords (debug, implement, refactor, etc.)
- Length thresholds: MaxSimpleChars=160, MaxSimpleWords=28
- Routes only genuinely simple messages to CheapModel

## Key Files
- `src/LLM/ichatclient.cs` -- IChatClient, LlmConfig
- `src/LLM/openaiclient.cs` -- OpenAI-compatible client (~470 lines)
- `src/LLM/AnthropicClient.cs` -- Anthropic Claude client (~420 lines)
- `src/LLM/CredentialPool.cs` -- multi-key rotation (~207 lines)
- `src/LLM/StreamTypes.cs` -- StreamEvent, UsageStats, StreamedMessage
- `src/LLM/ModelRouter.cs` -- smart model routing

## See Also
- [[../entities/chat-client-interface]]
- [[../patterns/provider-fallback]]
