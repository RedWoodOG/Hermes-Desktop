---
title: Gateway & Integrations
type: system
tags: [gateway, telegram, discord, messaging]
created: 2026-04-09
updated: 2026-04-09
sources: [src/gateway/GatewayService.cs, src/gateway/IPlatformAdapter.cs, src/gateway/GatewayModels.cs, src/gateway/platforms/TelegramAdapter.cs, src/gateway/platforms/DiscordAdapter.cs]
---

# Gateway & Integrations

Multi-platform messaging hub routing messages to/from the agent.

## GatewayService

Long-running background service managing platform adapter lifecycles.

### Startup (`StartAsync`)

1. Iterates configured adapters
2. Skips disabled platforms
3. Sets message handler and error handler on each adapter
4. Attempts connection -- success adds to `_adapters`, failure queues for retry
5. Starts background watchers: session expiry (5 min interval), platform reconnect (30s interval)

### Message Pipeline

`HandleMessageAsync(MessageEvent, ct)`:
1. **Authorization** -- 5-tier check (see below)
2. **Stale agent detection** -- if agent processing for >600s, evict and allow new request
3. **Command dispatch** -- `/new`, `/stop`, `/status`, `/help`
4. **Route to agent** -- calls registered `_agentHandler(sessionKey, text, platform)`
5. Active agent tracking prevents concurrent processing per session

### 5-Tier Authorization

| Tier | Check |
|------|-------|
| 1 | Platform exemptions: Webhook, API always allowed |
| 2 | Per-platform `allow_all_users` flag |
| 3 | Global `AllowedUsers` comma-separated list |
| 4 | Per-platform `allowed_users` list |
| 5 | DMs allowed by default when no allowlist configured |

### Session Management

Session key format: `{platform}:{chatId}` (optionally `:{userId}` when `GroupSessionsPerUser` is true).

### Reconnection

Exponential backoff: 30s -> 60s -> 120s -> capped at 300s. Max 20 retries before giving up.

### Outbound Messaging

`SendAsync(OutboundMessage, ct)` and `SendTextAsync(platform, chatId, text, ct)` for SendMessageTool and cron delivery.

## IPlatformAdapter Interface

5 methods (upstream: `BasePlatformAdapter`):
- `ConnectAsync(ct) -> bool`
- `SendAsync(OutboundMessage, ct) -> DeliveryResult`
- `DisconnectAsync()`
- `SetMessageHandler(Func<MessageEvent, Task<string?>>)`
- `SetErrorHandler(Action<Platform, Exception>)`

Implements `IAsyncDisposable`.

## Platform Adapters

- **TelegramAdapter** (`src/gateway/platforms/TelegramAdapter.cs`) -- Telegram Bot API
- **DiscordAdapter** (`src/gateway/platforms/DiscordAdapter.cs`) -- Discord bot gateway

## Config (config.yaml)

Integration tokens stored under `platforms:` section:
```yaml
platforms:
  telegram:
    token: bot-token-here
    allowed_users: user1,user2
  discord:
    token: bot-token-here
```

HermesEnvironment checks: env vars (`TELEGRAM_BOT_TOKEN`, `DISCORD_BOT_TOKEN`, etc.) OR config.yaml platform settings.

## Key Files
- `src/gateway/GatewayService.cs` -- main service (~430 lines)
- `src/gateway/IPlatformAdapter.cs` -- adapter interface
- `src/gateway/GatewayModels.cs` -- Platform enum, MessageEvent, OutboundMessage, DeliveryResult, GatewayConfig
- `src/gateway/platforms/TelegramAdapter.cs` -- Telegram implementation
- `src/gateway/platforms/DiscordAdapter.cs` -- Discord implementation

## See Also
- [[settings-config]]
- [[agent-loop]]
