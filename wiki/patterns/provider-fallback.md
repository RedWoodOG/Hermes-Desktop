---
title: Provider Fallback Pattern
type: pattern
tags: [llm, credentials, resilience]
created: 2026-04-09
updated: 2026-04-09
sources: [src/Core/agent.cs, src/LLM/CredentialPool.cs, src/LLM/openaiclient.cs]
---

# Provider Fallback Pattern

INV-004/005: Two-layer resilience -- credential rotation within a provider, then provider-level fallback.

## Layer 1: Credential Pool Rotation

CredentialPool manages multiple API keys per provider:

1. **Selection** -- `GetNext()` picks by strategy (LeastUsed default)
2. **On 401/403** -- `MarkFailed(key, statusCode, "auth_error")`, retry with next key
3. **On 429** -- `MarkFailed(key, 429, "rate_limited")`, retry with next key
4. **Max retries** -- 3 attempts per request in `OpenAiClient.PostAsync`
5. **Recovery** -- after cooldown (1h for 429, 24h for auth), credential becomes healthy again

```csharp
// In OpenAiClient.PostAsync
for (int attempt = 0; attempt < maxRetries; attempt++)
{
    var apiKey = _credentialPool.GetNext();
    // ... make request ...
    if (response.StatusCode == HttpStatusCode.Unauthorized)
    {
        _credentialPool.MarkFailed(apiKey, 401, "auth_error");
        continue; // Retry with next key
    }
}
```

## Layer 2: Provider Fallback

Agent maintains primary + fallback IChatClient:

1. **Normal** -- `GetActiveChatClient()` returns primary
2. **On HttpRequestException** -- `ActivateFallback(ex)` switches to fallback, records timestamp
3. **Restoration** -- every `PrimaryRestorationInterval` (5 min), if CredentialPool has healthy credentials, switch back to primary
4. **No fallback** -- if `_fallbackChatClient` is null, exception propagates

```csharp
private IChatClient GetActiveChatClient()
{
    if (_usingFallback && _fallbackActivatedAt.HasValue)
    {
        if (DateTime.UtcNow - _fallbackActivatedAt.Value >= PrimaryRestorationInterval)
        {
            if (_credentialPool is null || _credentialPool.HasHealthyCredentials)
            {
                _usingFallback = false;
                return _chatClient;
            }
        }
        return _fallbackChatClient ?? _chatClient;
    }
    return _chatClient;
}
```

## Lease System

For concurrent tool execution:
- `AcquireLease()` -- prefers credentials below `MaxConcurrentLeases` soft cap
- `ReleaseLease(apiKey)` -- decrements lease count
- Prevents all parallel tools from hitting the same key

## Configuration

```yaml
credential_pool:
  strategy: least_used  # round_robin, random, fill_first
  keys:
    - sk-key1
    - sk-key2
```

## When to Apply

- Multiple API keys available for same provider
- Primary provider may be unreliable
- Need graceful degradation without user-visible errors

## Key Files
- `src/Core/agent.cs` -- GetActiveChatClient, ActivateFallback (~lines 99-137)
- `src/LLM/CredentialPool.cs` -- pool management (~207 lines)
- `src/LLM/openaiclient.cs` -- PostAsync with rotation (~lines 244-293)

## See Also
- [[../systems/llm-providers]]
- [[../systems/agent-loop]]
