namespace Hermes.Agent.Dreamer;

using System.Globalization;
using Hermes.Agent.LLM;

/// <summary>Configuration for the Dreamer background worker (dreamer: section in config.yaml).</summary>
public sealed class DreamerConfig
{
    public bool Enabled { get; set; }
    public string WalkProvider { get; set; } = "ollama";
    public string WalkModel { get; set; } = "qwen3.5:latest";
    public string WalkBaseUrl { get; set; } = "http://127.0.0.1:11434/v1";
    public double WalkTemperature { get; set; } = 1.1;
    public int WalkMaxTokens { get; set; } = 2048;
    public string BuildProvider { get; set; } = "openai";
    public string BuildModel { get; set; } = "gpt-5.4-mini";
    public string? BuildBaseUrl { get; set; }
    public int WalkIntervalMinutes { get; set; } = 30;
    public IReadOnlyList<string> DigestTimes { get; set; } = ["08:00", "12:00", "20:00"];
    public string DiscordChannelId { get; set; } = "";
    public double TriggerThreshold { get; set; } = 7.0;
    public int MinWalksToTrigger { get; set; } = 4;
    public string Autonomy { get; set; } = "full"; // full | drafts | ideas
    public bool InputTranscripts { get; set; } = true;
    public bool InputInbox { get; set; } = true;
    public IReadOnlyList<string> RssFeeds { get; set; } = [];

    public static string ResolveHermesHome() =>
        Environment.GetEnvironmentVariable("HERMES_HOME") is { Length: > 0 } h
            ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes");

    /// <summary>Load dreamer: section from config.yaml (flat keys under dreamer:).</summary>
    public static DreamerConfig Load(string configPath)
    {
        var c = new DreamerConfig();
        if (!File.Exists(configPath))
            return c;

        var kv = ReadDreamerSection(configPath);
        if (kv.Count == 0)
            return c;

        static bool ParseBool(string? v, bool def)
        {
            if (string.IsNullOrWhiteSpace(v)) return def;
            return v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   v.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                   v.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        c.Enabled = ParseBool(Get(kv, "enabled"), false);
        c.WalkProvider = Get(kv, "walk_provider") ?? c.WalkProvider;
        c.WalkModel = Get(kv, "walk_model") ?? c.WalkModel;
        c.WalkBaseUrl = Get(kv, "walk_base_url") ?? c.WalkBaseUrl;
        if (double.TryParse(Get(kv, "walk_temperature"), NumberStyles.Float, CultureInfo.InvariantCulture, out var wt))
            c.WalkTemperature = wt;
        if (int.TryParse(Get(kv, "walk_max_tokens"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var wmt))
            c.WalkMaxTokens = wmt;
        c.BuildProvider = Get(kv, "build_provider") ?? c.BuildProvider;
        c.BuildModel = Get(kv, "build_model") ?? c.BuildModel;
        c.BuildBaseUrl = Get(kv, "build_base_url");
        if (int.TryParse(Get(kv, "walk_interval_minutes"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var wim))
            c.WalkIntervalMinutes = Math.Clamp(wim, 1, 24 * 60);
        var digest = Get(kv, "digest_times");
        if (!string.IsNullOrWhiteSpace(digest))
        {
            var parts = digest.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var validated = new List<string>();
            foreach (var part in parts)
            {
                var timeParts = part.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (timeParts.Length == 2 &&
                    int.TryParse(timeParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) &&
                    int.TryParse(timeParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) &&
                    h >= 0 && h <= 23 && m >= 0 && m <= 59)
                {
                    validated.Add(part);
                }
            }
            c.DigestTimes = validated;
        }
        c.DiscordChannelId = Get(kv, "discord_channel_id") ?? "";
        if (double.TryParse(Get(kv, "trigger_threshold"), NumberStyles.Float, CultureInfo.InvariantCulture, out var th))
            c.TriggerThreshold = th;
        if (int.TryParse(Get(kv, "min_walks_to_trigger"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mwt))
            c.MinWalksToTrigger = Math.Max(1, mwt);
        c.Autonomy = Get(kv, "autonomy") ?? c.Autonomy;
        c.InputTranscripts = ParseBool(Get(kv, "input_transcripts"), true);
        c.InputInbox = ParseBool(Get(kv, "input_inbox"), true);
        var rss = Get(kv, "rss_feeds");
        if (!string.IsNullOrWhiteSpace(rss))
            c.RssFeeds = rss.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        return c;
    }

    private static string? Get(Dictionary<string, string> kv, string key) =>
        kv.TryGetValue(key, out var v) ? v.Trim() : null;

    private static Dictionary<string, string> ReadDreamerSection(string configPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool inDreamer = false;
        foreach (var raw in File.ReadAllLines(configPath))
        {
            var trimmed = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.TrimStart().StartsWith("#", StringComparison.Ordinal))
                continue;

            if (raw.Length > 0 && !char.IsWhiteSpace(raw[0]))
            {
                if (trimmed.Equals("dreamer:", StringComparison.OrdinalIgnoreCase))
                {
                    inDreamer = true;
                    continue;
                }

                if (inDreamer && trimmed.EndsWith(':'))
                    break;

                inDreamer = false;
                continue;
            }

            if (!inDreamer) continue;

            var line = trimmed;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var val = line[(colon + 1)..].Trim().Trim('"', '\'');
            result[key] = val;
        }

        return result;
    }

    public LlmConfig ToWalkLlmConfig() =>
        new()
        {
            Provider = WalkProvider,
            Model = WalkModel,
            BaseUrl = WalkBaseUrl,
            Temperature = WalkTemperature,
            MaxTokens = WalkMaxTokens,
            ApiKey = "",
            AuthMode = "none"
        };

    public LlmConfig ToEchoLlmConfig()
    {
        var w = ToWalkLlmConfig();
        return new LlmConfig
        {
            Provider = w.Provider,
            Model = w.Model,
            BaseUrl = w.BaseUrl,
            Temperature = 0.2,
            MaxTokens = 1024,
            ApiKey = w.ApiKey,
            AuthMode = "none"
        };
    }
}