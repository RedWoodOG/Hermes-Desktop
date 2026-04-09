using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace HermesDesktop.Services;

internal static class HermesEnvironment
{
    internal static bool ShowLocalDetailsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("HERMES_DESKTOP_SHOW_LOCAL_DETAILS"),
            "1",
            StringComparison.Ordinal) ||
        File.Exists(Path.Combine(AppContext.BaseDirectory, "show-local-details.flag"));

    internal static bool PrivacyModeEnabled => !ShowLocalDetailsEnabled;

    internal static string HermesHomePath =>
        Environment.GetEnvironmentVariable("HERMES_HOME") is { Length: > 0 } configuredHome
            ? configuredHome
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes");

    internal static string HermesConfigPath => Path.Combine(HermesHomePath, "config.yaml");

    internal static string HermesLogsPath => Path.Combine(HermesHomePath, "logs");

    internal static string HermesWorkspacePath => Path.Combine(HermesHomePath, "hermes-agent");

    internal static string HermesCommandPath => Path.Combine(HermesHomePath, "bin", "hermes.cmd");

    internal static bool HermesInstalled => File.Exists(HermesCommandPath);

    // ── Soul system paths ──
    internal static string SoulDir => Path.Combine(HermesHomePath, "soul");
    internal static string SoulFilePath => Path.Combine(HermesHomePath, "SOUL.md");
    internal static string UserFilePath => Path.Combine(HermesHomePath, "USER.md");
    internal static string MistakesFilePath => Path.Combine(SoulDir, "mistakes.jsonl");
    internal static string HabitsFilePath => Path.Combine(SoulDir, "habits.jsonl");
    internal static string ProjectAgentsPath(string projectDir) =>
        Path.Combine(HermesHomePath, "projects", Path.GetFileName(projectDir), "AGENTS.md");

    /// <summary>Create LlmConfig from config.yaml for DI.</summary>
    internal static Hermes.Agent.LLM.LlmConfig CreateLlmConfig() => new()
    {
        Provider = ModelProvider,
        Model = DefaultModel,
        BaseUrl = ModelBaseUrl,
        ApiKey = ModelApiKey ?? ""
    };

    /// <summary>
    /// Load credential pool from config.yaml credential_pool: section.
    /// Returns null if no pool is configured.
    /// Expected format:
    ///   credential_pool:
    ///     strategy: least_used
    ///     keys:
    ///       - sk-key1
    ///       - sk-key2
    /// </summary>
    internal static Hermes.Agent.LLM.CredentialPool? LoadCredentialPool()
    {
        if (!File.Exists(HermesConfigPath))
            return null;

        bool inPoolSection = false;
        bool inKeysSection = false;
        var keys = new List<string>();
        string strategy = "least_used";

        foreach (string rawLine in File.ReadLines(HermesConfigPath))
        {
            string line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                continue;

            // Top-level section detection
            if (!char.IsWhiteSpace(rawLine, 0) && line.EndsWith(":", StringComparison.Ordinal))
            {
                inPoolSection = string.Equals(line, "credential_pool:", StringComparison.OrdinalIgnoreCase);
                inKeysSection = false;
                continue;
            }

            if (!inPoolSection) continue;

            string trimmed = line.Trim();

            if (trimmed.StartsWith("strategy:", StringComparison.OrdinalIgnoreCase))
            {
                strategy = trimmed["strategy:".Length..].Trim().Trim('"', '\'');
                continue;
            }

            if (trimmed == "keys:")
            {
                inKeysSection = true;
                continue;
            }

            if (inKeysSection && trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                var key = trimmed[2..].Trim().Trim('"', '\'');
                if (!string.IsNullOrEmpty(key))
                    keys.Add(key);
            }
        }

        if (keys.Count == 0) return null;

        var pool = new Hermes.Agent.LLM.CredentialPool
        {
            Strategy = strategy.ToLowerInvariant() switch
            {
                "round_robin" => Hermes.Agent.LLM.PoolStrategy.RoundRobin,
                "random" => Hermes.Agent.LLM.PoolStrategy.Random,
                "fill_first" => Hermes.Agent.LLM.PoolStrategy.FillFirst,
                _ => Hermes.Agent.LLM.PoolStrategy.LeastUsed
            }
        };

        foreach (var key in keys)
            pool.Add(key);

        return pool;
    }

    internal static bool TelegramConfigured => HasEnvironmentVariable("TELEGRAM_BOT_TOKEN");

    internal static bool DiscordConfigured => HasEnvironmentVariable("DISCORD_BOT_TOKEN");

    internal static bool SlackConfigured =>
        HasEnvironmentVariable("SLACK_BOT_TOKEN") ||
        !string.IsNullOrWhiteSpace(ReadPlatformSetting("slack", "token"));

    internal static bool WhatsAppConfigured =>
        HasEnvironmentVariable("WHATSAPP_ENABLED") ||
        string.Equals(ReadPlatformSetting("whatsapp", "enabled"), "true", StringComparison.OrdinalIgnoreCase);

    internal static bool MatrixConfigured =>
        HasEnvironmentVariable("MATRIX_ACCESS_TOKEN") ||
        !string.IsNullOrWhiteSpace(ReadPlatformSetting("matrix", "token"));

    internal static bool WebhookConfigured =>
        HasEnvironmentVariable("WEBHOOK_ENABLED") ||
        string.Equals(ReadPlatformSetting("webhook", "enabled"), "true", StringComparison.OrdinalIgnoreCase);

    internal static bool HasAnyMessagingToken =>
        TelegramConfigured || DiscordConfigured || SlackConfigured ||
        WhatsAppConfigured || MatrixConfigured || WebhookConfigured;

    /// <summary>Path to the gateway PID file.</summary>
    internal static string GatewayPidPath => Path.Combine(HermesHomePath, "gateway.pid");

    /// <summary>Path to the gateway runtime state file.</summary>
    internal static string GatewayStatePath => Path.Combine(HermesHomePath, "gateway_state.json");

    /// <summary>Check if the hermes-gateway process is currently running.</summary>
    internal static bool IsGatewayRunning()
    {
        try
        {
            if (!File.Exists(GatewayPidPath))
                return false;

            string raw = File.ReadAllText(GatewayPidPath).Trim();
            int pid;

            // PID file can be JSON or plain int
            if (raw.StartsWith("{", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(raw);
                if (!doc.RootElement.TryGetProperty("pid", out var pidProp))
                    return false;
                pid = pidProp.GetInt32();
            }
            else
            {
                if (!int.TryParse(raw, out pid))
                    return false;
            }

            var proc = Process.GetProcessById(pid);
            // Verify the process looks like a gateway
            string name = proc.ProcessName.ToLowerInvariant();
            return name.Contains("python") || name.Contains("hermes") || name.Contains("gateway");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Read the gateway runtime state JSON if available.</summary>
    internal static string ReadGatewayState()
    {
        try
        {
            if (!File.Exists(GatewayStatePath))
                return "unknown";

            string raw = File.ReadAllText(GatewayStatePath).Trim();
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("gateway_state", out var state))
                return state.GetString() ?? "unknown";
        }
        catch { }
        return "unknown";
    }

    /// <summary>Start the hermes gateway process.</summary>
    internal static void StartGateway()
    {
        if (!HermesInstalled) return;

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-WindowStyle Hidden -ExecutionPolicy Bypass -Command \"& '{HermesCommandPath}' gateway run\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    /// <summary>Stop the hermes gateway process.</summary>
    internal static void StopGateway()
    {
        try
        {
            if (!File.Exists(GatewayPidPath))
                return;

            string raw = File.ReadAllText(GatewayPidPath).Trim();
            int pid;

            if (raw.StartsWith("{", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(raw);
                if (!doc.RootElement.TryGetProperty("pid", out var pidProp))
                    return;
                pid = pidProp.GetInt32();
            }
            else
            {
                if (!int.TryParse(raw, out pid))
                    return;
            }

            var proc = Process.GetProcessById(pid);
            proc.Kill();
        }
        catch { }
    }

    /// <summary>Read a setting from a platform sub-section of config.yaml (platforms.{platform}.{key}).</summary>
    internal static string? ReadPlatformSetting(string platform, string key)
    {
        if (!File.Exists(HermesConfigPath))
            return null;

        // Parse: platforms:\n  {platform}:\n    {key}: value
        bool inPlatforms = false;
        bool inPlatform = false;
        string platformHeader = $"{platform}:";

        foreach (string rawLine in File.ReadLines(HermesConfigPath))
        {
            string line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                continue;

            // Top-level section detection
            if (!char.IsWhiteSpace(rawLine, 0) && line.EndsWith(":", StringComparison.Ordinal))
            {
                inPlatforms = string.Equals(line, "platforms:", StringComparison.OrdinalIgnoreCase);
                inPlatform = false;
                continue;
            }

            if (!inPlatforms) continue;

            string trimmed = line.Trim();

            // Detect platform subsection (2-space indent)
            if (rawLine.StartsWith("  ", StringComparison.Ordinal) &&
                !rawLine.StartsWith("    ", StringComparison.Ordinal) &&
                trimmed.EndsWith(":", StringComparison.Ordinal))
            {
                inPlatform = trimmed.Equals(platformHeader, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inPlatform) continue;

            // Read key within platform (4-space indent)
            string prefix = $"{key}:";
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return trimmed[prefix.Length..].Trim().Trim('"', '\'');
        }

        return null;
    }

    /// <summary>Write a token/setting to config.yaml under platforms.{platform}.{key}.</summary>
    internal static async Task SavePlatformSettingAsync(string platform, string key, string value)
    {
        var configPath = HermesConfigPath;
        var dir = Path.GetDirectoryName(configPath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var lines = File.Exists(configPath)
            ? (await File.ReadAllLinesAsync(configPath)).ToList()
            : new List<string>();

        // Find or create platforms: section
        int platformsSectionStart = -1;
        int platformsSectionEnd = lines.Count;
        for (int i = 0; i < lines.Count; i++)
        {
            string raw = lines[i];
            string trimmed = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            if (!char.IsWhiteSpace(raw, 0) && trimmed.EndsWith(":", StringComparison.Ordinal))
            {
                if (string.Equals(trimmed, "platforms:", StringComparison.OrdinalIgnoreCase))
                {
                    platformsSectionStart = i;
                }
                else if (platformsSectionStart >= 0 && platformsSectionEnd == lines.Count)
                {
                    platformsSectionEnd = i;
                }
            }
        }

        if (platformsSectionStart < 0)
        {
            // Append platforms: section
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add("");
            lines.Add("platforms:");
            platformsSectionStart = lines.Count - 1;
            platformsSectionEnd = lines.Count;
        }

        // Find or create the platform subsection
        int platStart = -1;
        int platEnd = platformsSectionEnd;
        string platHeader = $"  {platform}:";

        for (int i = platformsSectionStart + 1; i < platformsSectionEnd; i++)
        {
            string raw = lines[i];
            string trimmed = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            // 2-space indent platform header
            if (raw.StartsWith("  ", StringComparison.Ordinal) &&
                !raw.StartsWith("    ", StringComparison.Ordinal) &&
                trimmed.Trim().EndsWith(":", StringComparison.Ordinal))
            {
                if (string.Equals(trimmed.TrimEnd(), platHeader, StringComparison.OrdinalIgnoreCase))
                {
                    platStart = i;
                }
                else if (platStart >= 0 && platEnd == platformsSectionEnd)
                {
                    platEnd = i;
                }
            }
        }

        if (platStart < 0)
        {
            // Append platform subsection at end of platforms section
            int insertAt = platformsSectionEnd;
            lines.Insert(insertAt, platHeader);
            platStart = insertAt;
            platEnd = insertAt + 1;
            // Adjust platformsSectionEnd since we inserted
        }

        // Find or create the key line within the platform
        bool found = false;
        string keyPrefix = $"{key}:";
        for (int i = platStart + 1; i < platEnd; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"    {key}: {QuoteYamlValue(value)}";
                found = true;
                break;
            }
        }

        if (!found)
        {
            lines.Insert(platStart + 1, $"    {key}: {QuoteYamlValue(value)}");
        }

        await File.WriteAllLinesAsync(configPath, lines);
    }

    internal static string ModelProvider => ReadModelSetting("provider") ?? "custom";

    internal static string ModelBaseUrl => ReadModelSetting("base_url") ?? "http://127.0.0.1:11434/v1";

    internal static string DefaultModel => ReadModelSetting("default") ?? "minimax-m2.7:cloud";

    internal static string DisplayModelProvider =>
        PrivacyModeEnabled ? "configured" : ModelProvider;

    internal static string DisplayModelBaseUrl =>
        PrivacyModeEnabled ? "Configured local endpoint" : FormatPathForDisplay(ModelBaseUrl);

    internal static string DisplayDefaultModel =>
        PrivacyModeEnabled ? "configured local model" : DefaultModel;

    internal static string DisplayHermesHomePath =>
        PrivacyModeEnabled ? "Local application data\\hermes" : FormatPathForDisplay(HermesHomePath);

    internal static string DisplayHermesConfigPath =>
        PrivacyModeEnabled ? "Local application data\\hermes\\config.yaml" : FormatPathForDisplay(HermesConfigPath);

    internal static string DisplayHermesLogsPath =>
        PrivacyModeEnabled ? "Local application data\\hermes\\logs" : FormatPathForDisplay(HermesLogsPath);

    internal static string DisplayHermesWorkspacePath =>
        PrivacyModeEnabled ? "Embedded Hermes workspace" : FormatPathForDisplay(HermesWorkspacePath);

    internal static string DisplayAgentWorkingDirectory =>
        PrivacyModeEnabled ? "Active project workspace" : FormatPathForDisplay(AgentWorkingDirectory);

    internal static string DisplayModelPort =>
        PrivacyModeEnabled ? "Configured" : ModelPortDisplay;

    internal static string? ModelApiKey => ReadModelSetting("api_key");

    internal static string AgentWorkingDirectory
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable("HERMES_DESKTOP_WORKSPACE");
            if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            {
                return configured;
            }

            string candidate = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                ".."));

            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }

    internal static string ModelPortDisplay
    {
        get
        {
            if (Uri.TryCreate(ModelBaseUrl, UriKind.Absolute, out Uri? uri))
            {
                return uri.Port.ToString(CultureInfo.CurrentCulture);
            }

            return "11434";
        }
    }

    internal static void LaunchHermesChat()
    {
        if (!HermesInstalled)
        {
            OpenHermesHome();
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoExit -ExecutionPolicy Bypass -Command \"& '{HermesCommandPath}' chat\"",
            UseShellExecute = true,
        });
    }

    internal static void OpenHermesHome()
    {
        OpenPath(HermesHomePath);
    }

    internal static void OpenConfig()
    {
        OpenPath(File.Exists(HermesConfigPath) ? HermesConfigPath : HermesHomePath);
    }

    internal static void OpenLogs()
    {
        OpenPath(Directory.Exists(HermesLogsPath) ? HermesLogsPath : HermesHomePath);
    }

    internal static void OpenWorkspace()
    {
        OpenPath(Directory.Exists(HermesWorkspacePath) ? HermesWorkspacePath : HermesHomePath);
    }

    private static bool HasEnvironmentVariable(string key)
    {
        return Environment.GetEnvironmentVariable(key) is { Length: > 0 };
    }

    private static void OpenPath(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }

    private static string FormatPathForDisplay(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile) &&
            value.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
        {
            return "~" + value[userProfile.Length..];
        }

        return value;
    }

    /// <summary>Write an integration token to config.yaml under the integrations section.</summary>
    internal static async Task SaveIntegrationTokenAsync(string key, string value)
    {
        var configPath = HermesConfigPath;
        var dir = Path.GetDirectoryName(configPath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var settings = new Dictionary<string, string>
        {
            [key] = value,
        };

        await WriteYamlSectionAsync(configPath, "integrations", settings);
    }

    /// <summary>Read a value from the integrations section of config.yaml.</summary>
    internal static string? ReadIntegrationSetting(string key) => ReadConfigSetting("integrations", key);

    /// <summary>Read a value from any top-level section of config.yaml (section.key).</summary>
    internal static string? ReadConfigSetting(string section, string key)
    {
        if (!File.Exists(HermesConfigPath))
            return null;

        bool inSection = false;
        string sectionHeader = $"{section}:";
        foreach (string rawLine in File.ReadLines(HermesConfigPath))
        {
            string line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                continue;

            // Any non-indented line is a section boundary (whether it ends with ':' or not)
            if (rawLine.Length > 0 && !char.IsWhiteSpace(rawLine, 0))
            {
                inSection = line.EndsWith(":", StringComparison.Ordinal) &&
                            string.Equals(line, sectionHeader, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection) continue;

            string trimmed = line.Trim();
            string prefix = $"{key}:";
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return trimmed[prefix.Length..].Trim().Trim('"', '\'');
        }

        return null;
    }

    /// <summary>Save settings to any top-level section of config.yaml.</summary>
    internal static async Task SaveConfigSectionAsync(string section, Dictionary<string, string> settings)
    {
        var configPath = HermesConfigPath;
        var dir = Path.GetDirectoryName(configPath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        await WriteYamlSectionAsync(configPath, section, settings);
    }

    /// <summary>Quote a YAML value if it contains special characters.</summary>
    private static string QuoteYamlValue(string val)
    {
        if (string.IsNullOrEmpty(val)) return "\"\"";
        if (val.Contains('#') || val.Contains(": ") || val.Contains('{') || val.Contains('}') ||
            val.Contains('[') || val.Contains(']') || val.StartsWith("'") || val.StartsWith("\"") ||
            val.StartsWith(" ") || val.EndsWith(" ") ||
            val.Contains('\n') || val.Contains('\r'))
        {
            return $"\"{val.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r")}\"";
        }
        return val;
    }

    private static async Task WriteYamlSectionAsync(string configPath, string sectionName, Dictionary<string, string> settings)
    {
        var lines = File.Exists(configPath)
            ? (await File.ReadAllLinesAsync(configPath)).ToList()
            : new List<string>();

        // Find section boundaries
        int sectionStart = -1;
        int sectionEnd = lines.Count;
        for (int i = 0; i < lines.Count; i++)
        {
            string raw = lines[i];
            string trimmed = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            if (!char.IsWhiteSpace(raw, 0) && trimmed.EndsWith(":", StringComparison.Ordinal))
            {
                if (string.Equals(trimmed, $"{sectionName}:", StringComparison.OrdinalIgnoreCase))
                {
                    sectionStart = i;
                }
                else if (sectionStart >= 0 && sectionEnd == lines.Count)
                {
                    sectionEnd = i;
                }
            }
        }

        // Build new section lines — quote values containing YAML-special characters
        var newSection = new List<string> { $"{sectionName}:" };
        foreach (var kv in settings)
        {
            newSection.Add($"  {kv.Key}: {QuoteYamlValue(kv.Value)}");
        }

        if (sectionStart >= 0)
        {
            // Replace existing section
            lines.RemoveRange(sectionStart, sectionEnd - sectionStart);
            lines.InsertRange(sectionStart, newSection);
        }
        else
        {
            // Append new section
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add("");
            lines.AddRange(newSection);
        }

        await File.WriteAllLinesAsync(configPath, lines);
    }

    private static string? ReadModelSetting(string key)
    {
        if (!File.Exists(HermesConfigPath))
        {
            return null;
        }

        bool inModelSection = false;

        foreach (string rawLine in File.ReadLines(HermesConfigPath))
        {
            string line = rawLine.TrimEnd();

            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            if (!char.IsWhiteSpace(rawLine, 0) && line.EndsWith(":", StringComparison.Ordinal))
            {
                inModelSection = string.Equals(line, "model:", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inModelSection)
            {
                continue;
            }

            string trimmed = line.Trim();
            string prefix = $"{key}:";

            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return trimmed[prefix.Length..].Trim().Trim('"', '\'');
        }

        return null;
    }
}
