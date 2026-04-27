namespace Hermes.Agent.LLM;

using Microsoft.Extensions.Logging;

/// <summary>
/// Factory that creates the correct IChatClient on demand based on the current provider/model.
/// Pattern matches Claude Code: fresh client per request, model read from state at call time.
/// Supports runtime model swapping without app restart.
/// </summary>
public sealed class ChatClientFactory
{
    private readonly HttpClient _httpClient;
    private readonly CredentialPool? _credentialPool;
    private readonly ILogger<ChatClientFactory> _logger;

    // Current active config — mutable, changed via SwitchProvider
    private LlmConfig _currentConfig;
    private IChatClient _currentClient;
    private readonly object _lock = new();

    public ChatClientFactory(
        LlmConfig initialConfig,
        HttpClient httpClient,
        ILogger<ChatClientFactory> logger,
        CredentialPool? credentialPool = null)
    {
        _currentConfig = initialConfig;
        _httpClient = httpClient;
        _credentialPool = credentialPool;
        _logger = logger;
        _currentClient = CreateClient(initialConfig);
    }

    /// <summary>Get the current active client.</summary>
    public IChatClient Current
    {
        get { lock (_lock) { return _currentClient; } }
    }

    /// <summary>Current provider name.</summary>
    public string CurrentProvider
    {
        get { lock (_lock) { return _currentConfig.Provider; } }
    }

    /// <summary>Current model name.</summary>
    public string CurrentModel
    {
        get { lock (_lock) { return _currentConfig.Model; } }
    }

    /// <summary>Current full config (read-only snapshot).</summary>
    public LlmConfig CurrentConfig
    {
        get { lock (_lock) { return _currentConfig; } }
    }

    /// <summary>
    /// Switch to a different provider/model at runtime. Creates a new client immediately.
    /// This is the equivalent of Claude Code's /model command.
    /// </summary>
    public void SwitchProvider(LlmConfig newConfig)
    {
        lock (_lock)
        {
            _logger.LogInformation(
                "Switching provider: {OldProvider}/{OldModel} → {NewProvider}/{NewModel}",
                _currentConfig.Provider, _currentConfig.Model,
                newConfig.Provider, newConfig.Model);

            _currentConfig = newConfig;
            _currentClient = CreateClient(newConfig);
        }

        ProviderChanged?.Invoke(this, newConfig);
    }

    /// <summary>Fired when the provider/model changes.</summary>
    public event EventHandler<LlmConfig>? ProviderChanged;

    private IChatClient CreateClient(LlmConfig config)
    {
        // Create a fresh HttpClient with the right auth headers for this provider
        // Note: we reuse the base HttpClient but headers are set per-client
        var provider = config.Provider?.ToLowerInvariant() ?? "openai";

        return provider switch
        {
            "anthropic" or "claude" or "minimax" => new AnthropicClient(config, CreateHttpClientForProvider(config), _credentialPool),
            _ => new OpenAiClient(config, CreateHttpClientForProvider(config), _credentialPool),
        };
    }

    private HttpClient CreateHttpClientForProvider(LlmConfig config)
    {
        // Each provider needs its own HttpClient with correct default headers
        // to avoid header conflicts between e.g. Anthropic (x-api-key) and OpenAI (Authorization: Bearer)
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        // Provider-specific headers are set by the client constructors,
        // but we need a clean HttpClient for each to avoid header accumulation
        return client;
    }
}
