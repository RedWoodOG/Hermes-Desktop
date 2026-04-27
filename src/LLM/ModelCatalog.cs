namespace Hermes.Agent.LLM;

/// <summary>
/// Static model catalog matching the official Hermes Agent Python models.py
/// and model_metadata.py. Provides provider-to-model lists with context lengths.
/// </summary>
public static class ModelCatalog
{
    /// <summary>A single model entry with its ID, display name, and context window size.</summary>
    public sealed record ModelEntry(string Id, string DisplayName, int ContextLength);

    /// <summary>
    /// All supported providers and their available models.
    /// Sourced from hermes_cli/models.py (_PROVIDER_MODELS) and agent/model_metadata.py (DEFAULT_CONTEXT_LENGTHS).
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<ModelEntry>> ProviderModels { get; } =
        new Dictionary<string, IReadOnlyList<ModelEntry>>
        {
            ["nous"] = new ModelEntry[]
            {
                new("anthropic/claude-opus-4.6",       "Claude Opus 4.6 (recommended)", 1_000_000),
                new("anthropic/claude-sonnet-4.6",     "Claude Sonnet 4.6",             1_000_000),
                new("qwen/qwen3.6-plus:free",          "Qwen 3.6 Plus (free)",          131_072),
                new("anthropic/claude-sonnet-4.5",     "Claude Sonnet 4.5",             200_000),
                new("anthropic/claude-haiku-4.5",      "Claude Haiku 4.5",              200_000),
                new("openai/gpt-5.4",                  "GPT-5.4",                       128_000),
                new("openai/gpt-5.4-mini",             "GPT-5.4 Mini",                  128_000),
                new("xiaomi/mimo-v2-pro",              "MiMo V2 Pro",                   1_048_576),
                new("openai/gpt-5.3-codex",            "GPT-5.3 Codex",                 128_000),
                new("google/gemini-3-pro-preview",     "Gemini 3 Pro",                  1_048_576),
                new("google/gemini-3-flash-preview",   "Gemini 3 Flash",                1_048_576),
                new("minimax/minimax-m2.7",            "MiniMax M2.7",                  204_800),
                new("minimax/minimax-m2.5",            "MiniMax M2.5",                  204_800),
                new("z-ai/glm-5",                      "GLM-5",                         202_752),
                new("z-ai/glm-5-turbo",                "GLM-5 Turbo",                   202_752),
                new("moonshotai/kimi-k2.5",            "Kimi K2.5",                     262_144),
                new("x-ai/grok-4.20-beta",             "Grok 4.20 Beta",                128_000),
                new("openai/gpt-5.4-pro",              "GPT-5.4 Pro",                   128_000),
                new("openai/gpt-5.4-nano",             "GPT-5.4 Nano",                  128_000),
            },

            ["openai"] = new ModelEntry[]
            {
                new("gpt-5.4",            "GPT-5.4",             128_000),
                new("gpt-5.4-mini",       "GPT-5.4 Mini",        128_000),
                new("gpt-5.4-pro",        "GPT-5.4 Pro",         128_000),
                new("gpt-5.4-nano",       "GPT-5.4 Nano",        128_000),
                new("gpt-5.3-codex",      "GPT-5.3 Codex",       128_000),
                new("gpt-5-mini",         "GPT-5 Mini",          128_000),
                new("gpt-4.1",            "GPT-4.1",            1_047_576),
                new("gpt-4o",             "GPT-4o",              128_000),
                new("gpt-4o-mini",        "GPT-4o Mini",         128_000),
            },

            ["google"] = new ModelEntry[]
            {
                new("models/gemini-3.1-pro-preview",             "Gemini 3.1 Pro Preview",              1_048_576),
                new("models/gemini-3.1-pro-preview-customtools", "Gemini 3.1 Pro Preview Custom Tools", 1_048_576),
                new("models/gemini-3.1-flash-lite-preview",      "Gemini 3.1 Flash Lite Preview",       1_048_576),
                new("models/gemini-3-pro-preview",               "Gemini 3 Pro Preview",                1_048_576),
                new("models/gemini-3-flash-preview",             "Gemini 3 Flash Preview",              1_048_576),
                new("models/gemini-2.5-pro",                     "Gemini 2.5 Pro",                      1_048_576),
                new("models/gemini-2.5-flash",                   "Gemini 2.5 Flash",                    1_048_576),
                new("models/gemini-pro-latest",                  "Gemini Pro Latest",                   1_048_576),
            },

            ["anthropic"] = new ModelEntry[]
            {
                new("claude-opus-4-6",              "Claude Opus 4.6",             1_000_000),
                new("claude-sonnet-4-6",            "Claude Sonnet 4.6",           1_000_000),
                new("claude-opus-4-5-20251101",     "Claude Opus 4.5 (Nov 2025)",    200_000),
                new("claude-sonnet-4-5-20250929",   "Claude Sonnet 4.5 (Sep 2025)",  200_000),
                new("claude-opus-4-20250514",       "Claude Opus 4 (May 2025)",      200_000),
                new("claude-sonnet-4-20250514",     "Claude Sonnet 4 (May 2025)",    200_000),
                new("claude-haiku-4-5-20251001",    "Claude Haiku 4.5 (Oct 2025)",   200_000),
            },

            ["qwen"] = new ModelEntry[]
            {
                new("qwen-max",                "Qwen Max",                32_768),
                new("qwen-max-latest",         "Qwen Max (Latest)",       32_768),
                new("qwen-plus",               "Qwen Plus",               131_072),
                new("qwen-plus-latest",        "Qwen Plus (Latest)",      131_072),
                new("qwen-turbo",              "Qwen Turbo",              131_072),
                new("qwen-turbo-latest",       "Qwen Turbo (Latest)",     131_072),
                new("qwen-long",               "Qwen Long",               10_000_000),
                new("qwen3-235b-a22b",         "Qwen3 235B",              131_072),
                new("qwen3-30b-a3b",           "Qwen3 30B",               131_072),
                new("qwen3-32b",               "Qwen3 32B",               131_072),
                new("qwen3-14b",               "Qwen3 14B",               131_072),
                new("qwen3-8b",                "Qwen3 8B",                131_072),
                new("qwen3-4b",                "Qwen3 4B",                131_072),
                new("qwen3-1.7b",              "Qwen3 1.7B",              131_072),
                new("qwen3-0.6b",              "Qwen3 0.6B",              131_072),
                new("qwen2.5-coder-32b-instruct", "Qwen2.5 Coder 32B",   131_072),
                new("qwen2.5-72b-instruct",    "Qwen2.5 72B",            131_072),
                new("qwq-32b",                 "QwQ 32B (Reasoning)",     131_072),
                new("qwq-plus",                "QwQ Plus (Reasoning)",    131_072),
            },

            ["deepseek"] = new ModelEntry[]
            {
                new("deepseek-chat",      "DeepSeek Chat",       128_000),
                new("deepseek-reasoner",  "DeepSeek Reasoner",   128_000),
            },

            ["minimax"] = new ModelEntry[]
            {
                new("MiniMax-M2.7",           "MiniMax M2.7",           204_800),
                new("MiniMax-M2.7-highspeed", "MiniMax M2.7 Highspeed", 204_800),
                new("MiniMax-M2.5",           "MiniMax M2.5",           204_800),
                new("MiniMax-M2.5-highspeed", "MiniMax M2.5 Highspeed", 204_800),
                new("MiniMax-M2.1",           "MiniMax M2.1",           204_800),
            },

            ["openrouter"] = new ModelEntry[]
            {
                new("anthropic/claude-opus-4.6",       "Claude Opus 4.6 (recommended)", 1_000_000),
                new("anthropic/claude-sonnet-4.6",     "Claude Sonnet 4.6",             1_000_000),
                new("qwen/qwen3.6-plus:free",          "Qwen 3.6 Plus (free)",          131_072),
                new("anthropic/claude-sonnet-4.5",     "Claude Sonnet 4.5",             200_000),
                new("openai/gpt-5.4",                  "GPT-5.4",                       128_000),
                new("openai/gpt-5.4-mini",             "GPT-5.4 Mini",                  128_000),
                new("google/gemini-3-pro-preview",     "Gemini 3 Pro",                  1_048_576),
                new("google/gemini-3-flash-preview",   "Gemini 3 Flash",                1_048_576),
                new("minimax/minimax-m2.7",            "MiniMax M2.7",                  204_800),
                new("x-ai/grok-4.20-beta",             "Grok 4.20 Beta",                128_000),
            },

            ["ollama"] = new ModelEntry[]
            {
                new("glm-5:cloud",              "GLM-5 (Cloud)",            128_000),
                new("minimax-m2.7:cloud",       "MiniMax M2.7 (Cloud)",     204_800),
                new("qwen3.5:cloud",            "Qwen 3.5 (Cloud)",        131_072),
                new("gemma4:31b",               "Gemma 4 31B (Local)",      32_768),
                new("gemma4:e4b",               "Gemma 4 E4B (Local)",      32_768),
                new("glm-4.7-flash:latest",     "GLM-4.7 Flash (Local)",    32_768),
                new("llama4:latest",            "Llama 4 (Local)",          128_000),
            },

            ["local"] = new ModelEntry[]
            {
                new("custom",  "Custom / Local Model", 128_000),
            },
        };

    /// <summary>All known provider names.</summary>
    public static IReadOnlyList<string> Providers { get; } = new[]
    {
        "nous", "openai", "google", "anthropic", "qwen", "deepseek", "minimax", "openrouter", "ollama", "local"
    };

    /// <summary>Default base URLs for known providers.</summary>
    public static IReadOnlyDictionary<string, string> ProviderBaseUrls { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai"]     = "https://api.openai.com/v1",
            ["google"]     = "https://generativelanguage.googleapis.com/v1beta/openai",
            ["anthropic"]  = "https://api.anthropic.com",
            ["qwen"]       = "https://dashscope.aliyuncs.com/compatible-mode/v1",
            ["deepseek"]   = "https://api.deepseek.com/v1",
            ["minimax"]    = "https://api.minimaxi.com/anthropic/v1",
            ["openrouter"] = "https://openrouter.ai/api/v1",
            ["nous"]       = "https://openrouter.ai/api/v1",
            ["ollama"]     = "http://127.0.0.1:11434/v1",
            ["local"]      = "http://127.0.0.1:11434/v1",
        };

    /// <summary>Get models for a provider, returning an empty list if unknown.</summary>
    public static IReadOnlyList<ModelEntry> GetModels(string provider)
    {
        return ProviderModels.TryGetValue(provider.ToLowerInvariant(), out var models)
            ? models
            : Array.Empty<ModelEntry>();
    }

    /// <summary>Look up context length for a model ID. Returns default 128k if unknown.</summary>
    public static int GetContextLength(string modelId)
    {
        foreach (var models in ProviderModels.Values)
        {
            foreach (var m in models)
            {
                if (string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase))
                    return m.ContextLength;
            }
        }
        return 128_000; // fallback
    }

    /// <summary>Format a context length as a human-readable string (e.g. "128K", "1M").</summary>
    public static string FormatContextLength(int contextLength)
    {
        if (contextLength >= 1_000_000)
            return $"{contextLength / 1_000_000.0:0.#}M tokens";
        if (contextLength >= 1_000)
            return $"{contextLength / 1_000}K tokens";
        return $"{contextLength} tokens";
    }
}
