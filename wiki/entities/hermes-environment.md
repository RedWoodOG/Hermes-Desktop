---
title: HermesEnvironment
type: entity
tags: [settings, config, desktop]
created: 2026-04-09
updated: 2026-04-09
sources: [Desktop/HermesDesktop/Services/HermesEnvironment.cs]
---

# HermesEnvironment

`Desktop/HermesDesktop/Services/HermesEnvironment.cs` (~725 lines) -- static class with all path resolution, config reading, platform detection, and gateway control.

## Path Properties

All static, computed on access:

```csharp
HermesHomePath      // %HERMES_HOME% or %LOCALAPPDATA%/hermes
HermesConfigPath    // {Home}/config.yaml
HermesLogsPath      // {Home}/logs
HermesWorkspacePath // {Home}/hermes-agent
HermesCommandPath   // {Home}/bin/hermes.cmd
SoulDir             // {Home}/soul
SoulFilePath        // {Home}/SOUL.md
UserFilePath        // {Home}/USER.md
MistakesFilePath    // {Home}/soul/mistakes.jsonl
HabitsFilePath      // {Home}/soul/habits.jsonl
AgentWorkingDirectory // %HERMES_DESKTOP_WORKSPACE% or 6-levels-up from AppContext.BaseDirectory
```

## Config Reading

Line-by-line YAML parser (no library dependency):

- `ReadModelSetting(key)` -- reads from `model:` section
- `ReadConfigSetting(section, key)` -- reads any `{section}:\n  {key}: value`
- `ReadPlatformSetting(platform, key)` -- reads `platforms:\n  {platform}:\n    {key}: value`
- `ReadIntegrationSetting(key)` -- reads from `integrations:` section

## Config Writing

- `SaveConfigSectionAsync(section, settings)` -- finds or creates section, writes key-value pairs
- `SavePlatformSettingAsync(platform, key, value)` -- nested platform config
- `SaveIntegrationTokenAsync(key, value)` -- writes to integrations section
- `WriteYamlSectionAsync(path, section, settings)` -- internal helper for section replacement

## QuoteYamlValue

Quotes values containing: `# : { } [ ]` or starting/ending with space or quotes. Escapes `\`, `"`, newlines.

## Gateway Controls

- `IsGatewayRunning()` -- reads PID file (JSON or plain int), verifies process by name
- `StartGateway()` -- launches `hermes gateway run` via powershell hidden window
- `StopGateway()` -- kills process by PID
- `ReadGatewayState()` -- reads `gateway_state.json` for state string
- `GatewayPidPath` / `GatewayStatePath` -- file locations

## Platform Detection

Boolean properties for each platform:
- `TelegramConfigured` -- checks `TELEGRAM_BOT_TOKEN` env var
- `DiscordConfigured` -- checks `DISCORD_BOT_TOKEN` env var
- `SlackConfigured` -- env var OR config.yaml `platforms.slack.token`
- `WhatsAppConfigured` -- env var OR config.yaml `platforms.whatsapp.enabled`
- `MatrixConfigured` -- env var OR config.yaml `platforms.matrix.token`
- `WebhookConfigured` -- env var OR config.yaml `platforms.webhook.enabled`
- `HasAnyMessagingToken` -- OR of all above

## Privacy Mode

`PrivacyModeEnabled` = !ShowLocalDetailsEnabled. When enabled, Display* properties mask sensitive info:
- `DisplayModelProvider` -> "configured"
- `DisplayModelBaseUrl` -> "Configured local endpoint"
- `DisplayDefaultModel` -> "configured local model"

## Credential Pool Factory

`LoadCredentialPool()` parses `credential_pool:` YAML section, returns CredentialPool with strategy and keys. Returns null if no pool configured.

## Key Files
- `Desktop/HermesDesktop/Services/HermesEnvironment.cs` -- this class

## See Also
- [[../systems/settings-config]]
- [[../systems/gateway-integrations]]
