---
title: Settings & Configuration
type: system
tags: [settings, config, desktop]
created: 2026-04-09
updated: 2026-04-09
sources: [Desktop/HermesDesktop/Services/HermesEnvironment.cs, Desktop/HermesDesktop/Views/SettingsPage.xaml.cs]
---

# Settings & Configuration

## HermesEnvironment (Static Class)

Central configuration hub at `Desktop/HermesDesktop/Services/HermesEnvironment.cs` (~725 lines). All properties are static.

### Path Resolution

| Property | Default |
|----------|---------|
| HermesHomePath | `%HERMES_HOME%` or `%LOCALAPPDATA%/hermes` |
| HermesConfigPath | `{HermesHome}/config.yaml` |
| HermesLogsPath | `{HermesHome}/logs` |
| HermesWorkspacePath | `{HermesHome}/hermes-agent` |
| HermesCommandPath | `{HermesHome}/bin/hermes.cmd` |
| SoulDir | `{HermesHome}/soul` |
| SoulFilePath | `{HermesHome}/SOUL.md` |
| UserFilePath | `{HermesHome}/USER.md` |

### Model Configuration (config.yaml `model:` section)

- `ModelProvider` -- `ReadModelSetting("provider")`, default "custom"
- `ModelBaseUrl` -- `ReadModelSetting("base_url")`, default "http://127.0.0.1:11434/v1"
- `DefaultModel` -- `ReadModelSetting("default")`, default "minimax-m2.7:cloud"
- `ModelApiKey` -- `ReadModelSetting("api_key")`
- `CreateLlmConfig()` -- factory method returning LlmConfig

### Gateway Controls

- `TelegramConfigured` / `DiscordConfigured` -- env var checks
- `SlackConfigured` / `WhatsAppConfigured` / `MatrixConfigured` / `WebhookConfigured` -- env vars + config.yaml
- `HasAnyMessagingToken` -- OR of all platform checks
- `IsGatewayRunning()` -- reads PID file, verifies process is alive
- `StartGateway()` / `StopGateway()` -- launches/kills via powershell

### Config YAML Operations

- `ReadConfigSetting(section, key)` -- line-by-line YAML parser for `section:\n  key: value`
- `SaveConfigSectionAsync(section, settings)` -- find-or-create section, write key-value pairs
- `ReadPlatformSetting(platform, key)` -- reads `platforms:{platform}:{key}`
- `SavePlatformSettingAsync(platform, key, value)` -- writes nested platform config
- `QuoteYamlValue(val)` -- escapes special YAML chars (# : { } [ ] etc.)

### Credential Pool Loading

`LoadCredentialPool()` parses `credential_pool:` section:
```yaml
credential_pool:
  strategy: least_used
  keys:
    - sk-key1
    - sk-key2
```
Returns `CredentialPool` with strategy mapping: least_used, round_robin, random, fill_first.

### Privacy Mode

`ShowLocalDetailsEnabled` -- requires env var `HERMES_DESKTOP_SHOW_LOCAL_DETAILS=1` or flag file.
When disabled (default), Display* properties mask paths and model names.

## Key Files
- `Desktop/HermesDesktop/Services/HermesEnvironment.cs` -- all config/path logic
- `Desktop/HermesDesktop/Views/SettingsPage.xaml.cs` -- Settings UI

## See Also
- [[../entities/hermes-environment]]
- [[gateway-integrations]]
